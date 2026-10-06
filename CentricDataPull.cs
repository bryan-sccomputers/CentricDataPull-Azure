using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CentricDataPull
{
    public static class CentricDataPull
    {
        private static string securityToken;
        private static DateTime expiresAt;
        private static readonly SemaphoreSlim TokenGate = new SemaphoreSlim(1, 1);
        public static string GetSecurityToken() => Volatile.Read(ref securityToken);
        public static void SetSecurityToken(HttpRequest req)
        {
            TokenGate.Wait(req.HttpContext.RequestAborted);
            try { securityToken = CentricAPI.AssignSecurityToken(req); SetNextExpiration(); }
            finally { TokenGate.Release(); }
        }
        public static bool SecurityTokenExists() => !string.IsNullOrWhiteSpace(GetSecurityToken());
        public static void CheckSecurityToken(HttpRequest req)
            => TokenAsync(req, req.HttpContext.RequestAborted).GetAwaiter().GetResult();
        public static DateTime GetNextExpiration() => expiresAt;
        public static void SetNextExpiration() => expiresAt = DateTime.UtcNow.AddHours(8);
        public static bool isExpired() => DateTime.UtcNow >= expiresAt;
        public static string UsesExtTable(string extTableName) => string.IsNullOrEmpty(extTableName) ? "" : "_" + extTableName;
        private static async Task<string> TokenAsync(HttpRequest req, CancellationToken cancellation)
        {
            // A caller-supplied token is never shared with other invocations.
            string supplied = req.Query["token"];
            if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
            using (CentricAPI.Measure("auth.lock.wait")) await TokenGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!SecurityTokenExists() || isExpired())
                {
                    using (CentricAPI.Measure("auth.session"))
                        securityToken = await CentricAPI.AssignSecurityTokenAsync(req, cancellation).ConfigureAwait(false);
                    SetNextExpiration();
                }
                return securityToken;
            }
            finally { TokenGate.Release(); }
        }
        private static string Value(HttpRequest req, JObject body, string name)
        {
            string query = req.Query[name];
            if (!string.IsNullOrEmpty(query)) return query;
            var value = body?[name];
            if (value == null || value.Type == JTokenType.Null) return null;
            if (value.Type != JTokenType.String) throw new ArgumentException("Request fields must be strings.");
            return (string)value;
        }
        private static JArray Records(string text, bool collection)
        {
            if (string.IsNullOrWhiteSpace(text)) return new JArray();
            var parsed = JToken.Parse(text);
            var obj = parsed as JObject;
            if (obj != null && (obj["error"] != null || obj["errors"] != null))
                throw new CentricAPI.ApiException(200, "API error envelope returned with success status.");
            var array = parsed as JArray;
            if (array == null && parsed is JObject)
            {
                // Opt-in only: avoids mistaking an error object for a record page.
                var field = Environment.GetEnvironmentVariable("CENTRIC_RESPONSE_ARRAY_PROPERTY");
                if (!string.IsNullOrWhiteSpace(field)) array = parsed[field] as JArray;
                if (array == null && !collection) array = new JArray(parsed);
            }
            if (array == null || array.Any(r => !(r is JObject)))
                throw new CentricAPI.ApiException(200, "Expected an array of objects or configured response envelope.");
            return array;
        }
        private static IActionResult Error(int status, string code, string id)
            => new ObjectResult(new { error = code, correlationId = id }) { StatusCode = status };

        [FunctionName("CentricDataPullLive")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req, ILogger log)
        {
            var id = Guid.NewGuid().ToString("N");
            var watch = Stopwatch.StartNew();
            using (log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            using (CentricAPI.UseLogger(log))
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(req.HttpContext.RequestAborted))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(CentricAPI.Setting("CENTRIC_RUN_TIMEOUT_SECONDS", 210)));
                var cancellation = budget.Token;
                log.LogInformation("Centric invocation started CorrelationId={CorrelationId}", id);
                try
                {
                    JObject body = null;
                    using (CentricAPI.Measure("request.body"))
                    using (var reader = new StreamReader(req.Body, Encoding.UTF8, true, 1024, true))
                    using (cancellation.Register(() => req.HttpContext.Abort()))
                    {
                        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(text)) body = JObject.Parse(text);
                    }
                    var endpoint = Value(req, body, "name");
                    if (string.IsNullOrWhiteSpace(endpoint)) return Error(400, "missing_name", id);
                    // The endpoint also becomes a SQL table name. Reject traversal and query injection here.
                    if (endpoint.Length > 128 || endpoint.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                        return Error(400, "invalid_name", id);
                    var element = Value(req, body, "element");
                    var extname = Value(req, body, "extname");
                    var tableName = endpoint + UsesExtTable(extname);
                    if (tableName.Length > 128) return Error(400, "invalid_table_name", id);
                    var parameters = Value(req, body, "parameters");
                    parameters = string.IsNullOrWhiteSpace(parameters) ? "" : "?" +
                        string.Join("&", parameters.Trim('"').TrimStart('?').Split(',').Where(p => !string.IsNullOrWhiteSpace(p)));
                    var token = await TokenAsync(req, cancellation).ConfigureAwait(false);
                    var records = new JArray();
                    string singleResponse = null;
                    if (string.IsNullOrEmpty(element))
                    {
                        var seen = new HashSet<string>();
                        var maxPages = CentricAPI.Setting("CENTRIC_MAX_PAGES", 1000, 100000);
                        // Preserve the original skip increment; verify it against the API page size contract.
                        var pageSize = CentricAPI.Setting("CENTRIC_PAGE_SIZE", 10, 100000);
                        var maxRecords = CentricAPI.Setting("CENTRIC_MAX_RECORDS", 100000, 10000000);
                        bool complete = false;
                        for (int page = 0; page < maxPages; page++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            CentricAPI.Detail("pagination", "page.start", watch.ElapsedMilliseconds, page);
                            var text = await CentricAPI.RequestAsync(endpoint + parameters +
                                (parameters.Length == 0 ? "?" : "&") + "skip=" + checked(page * pageSize), token, null, cancellation).ConfigureAwait(false);
                            var batch = Records(text, true);
                            CentricAPI.Detail("pagination", "page.rows", watch.ElapsedMilliseconds, batch.Count);
                            if (batch.Count == 0) { complete = true; break; }
                            string fingerprint;
                            using (var sha = SHA256.Create())
                                fingerprint = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(batch.ToString(Formatting.None))));
                            if (!seen.Add(fingerprint)) throw new CentricAPI.ApiException(200, "Repeated page detected.");
                            if ((long)records.Count + batch.Count > maxRecords)
                                throw new CentricAPI.ApiException(200, "Record safety limit exceeded.");
                            foreach (var row in batch) records.Add(row);
                        }
                        if (!complete) throw new CentricAPI.ApiException(200, "Page safety limit exceeded.");
                    }
                    else
                    {
                        var text = await CentricAPI.RequestAsync(endpoint + "/" + Uri.EscapeDataString(element), token, null, cancellation).ConfigureAwait(false);
                        records = Records(text, false);
                        singleResponse = string.IsNullOrWhiteSpace(text) ? "[]" : JToken.Parse(text).ToString(Formatting.None);
                    }
                    // Preserve single-record response shape and the original string-valued HTTP response.
                    string tableData = singleResponse ?? records.ToString(Formatting.None);
                    string insertData = records.ToString(Formatting.None);
                    await CentricAPI.ConvertAndInsertAsync(tableName, insertData, cancellation).ConfigureAwait(false);
                    log.LogInformation("Centric invocation succeeded CorrelationId={CorrelationId} Rows={Rows} ElapsedMs={ElapsedMs}",
                        id, records.Count, watch.ElapsedMilliseconds);
                    return new OkObjectResult(tableData);
                }
                catch (OperationCanceledException)
                {
                    log.LogWarning("Centric invocation canceled CorrelationId={CorrelationId} ClientAborted={ClientAborted} ElapsedMs={ElapsedMs}",
                        id, req.HttpContext.RequestAborted.IsCancellationRequested, watch.ElapsedMilliseconds);
                    return Error(504, "request_canceled_or_budget_exceeded", id);
                }
                catch (TimeoutException error) { CentricAPI.Failure("invocation", error); return Error(504, "upstream_timeout", id); }
                catch (CentricAPI.ApiException error)
                {
                    if (error.StatusCode == 401 || error.StatusCode == 403)
                    {
                        // Expire the shared session for the next invocation. Never cache a supplied token.
                        if (string.IsNullOrWhiteSpace((string)req.Query["token"]))
                        {
                            if (TokenGate.Wait(0))
                            {
                                try { securityToken = null; expiresAt = default(DateTime); }
                                finally { TokenGate.Release(); }
                            }
                        }
                    }
                    CentricAPI.Failure("invocation", error);
                    log.LogWarning("Centric upstream failure CorrelationId={CorrelationId} UpstreamStatus={UpstreamStatus}", id, error.StatusCode);
                    return Error(error.StatusCode == 429 || error.StatusCode >= 500 ? 503 : 502, "upstream_response_failure", id);
                }
                catch (JsonException error) { CentricAPI.Failure("request.json", error); return Error(400, "invalid_json", id); }
                catch (ArgumentException error) { CentricAPI.Failure("request.validation", error); return Error(400, "invalid_request", id); }
                catch (SqlException error) { CentricAPI.Failure("sql", error); return Error(error.Number == -2 ? 504 : 500, "database_failure", id); }
                catch (Exception error) { CentricAPI.Failure("invocation", error); return Error(500, "internal_failure", id); }
                finally { CentricAPI.Detail("invocation", "end", watch.ElapsedMilliseconds); }
            }
        }
    }
}
