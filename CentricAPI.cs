using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MimeKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace CentricDataPull
{
    public static class CentricAPI
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        private static readonly AsyncLocal<ILogger> Logger = new AsyncLocal<ILogger>();

        public static IDisposable UseLogger(ILogger logger)
        {
            var previous = Logger.Value;
            Logger.Value = logger;
            return new ActionScope(() => Logger.Value = previous);
        }
        private sealed class ActionScope : IDisposable
        {
            private readonly Action action;
            public ActionScope(Action action) { this.action = action; }
            public void Dispose() { action(); }
        }
        public static int Setting(string name, int fallback, int max = 3600)
        {
            int value;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out value) && value > 0 && value <= max
                ? value : fallback;
        }
        public static string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Missing configuration: " + name);
            return value;
        }
        public static void Detail(string operation, string phase, long elapsedMs = 0, int count = 0)
        {
            var enabled = Environment.GetEnvironmentVariable("CENTRIC_DETAILED_LOGGING");
            if (enabled == "1" || string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
                Logger.Value?.LogInformation("Centric Operation={Operation} Phase={Phase} ElapsedMs={ElapsedMs} Count={Count}",
                    operation, phase, elapsedMs, count);
        }
        public static void Failure(string operation, Exception error)
        {
            // Deliberately exclude exception messages/stack traces: providers can embed URLs, SQL or credentials.
            Logger.Value?.LogError("Centric Operation={Operation} ErrorType={ErrorType} SqlNumber={SqlNumber}",
                operation, error.GetType().Name, (error as SqlException)?.Number);
        }
        public static IDisposable Measure(string operation)
        {
            var watch = Stopwatch.StartNew();
            Detail(operation, "start");
            return new ActionScope(() => Detail(operation, "end", watch.ElapsedMilliseconds));
        }
        public sealed class ApiException : Exception
        {
            public int StatusCode { get; private set; }
            public ApiException(int statusCode, string category) : base(category) { StatusCode = statusCode; }
        }

        private static Uri ApiUri(string path)
        {
            Uri root;
            if (!Uri.TryCreate(Required("CENTRIC_BASE_URL").TrimEnd('/') + "/", UriKind.Absolute, out root)
                || root.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("CENTRIC_BASE_URL must be an absolute HTTPS URL.");
            var target = new Uri(root, path);
            if (target.Authority != root.Authority || !target.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))
                throw new ArgumentException("Endpoint must remain within the configured API base.");
            return target;
        }
        public static async Task<string> RequestAsync(string path, string token, string loginBody, CancellationToken cancellation)
        {
            var operation = loginBody == null ? "http.get" : "http.session";
            var attempts = loginBody == null ? Setting("CENTRIC_HTTP_ATTEMPTS", 3, 5) : 1;
            for (int attempt = 1; ; attempt++)
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var request = new HttpRequestMessage(loginBody == null ? HttpMethod.Get : HttpMethod.Post, ApiUri(path)))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(Setting("CENTRIC_HTTP_TIMEOUT_SECONDS", 30)));
                    if (!string.IsNullOrWhiteSpace(token)) request.Headers.Add("Cookie", token);
                    if (loginBody != null) request.Content = new StringContent(loginBody, Encoding.UTF8, "application/json");
                    var watch = Stopwatch.StartNew();
                    Detail(operation, "start", 0, attempt);
                    try
                    {
                        using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                        {
                            int status = (int)response.StatusCode;
                            Detail(operation, "response.status", watch.ElapsedMilliseconds, status);
                            bool transient = status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
                            if (transient && attempt < attempts)
                            {
                                var retry = response.Headers.RetryAfter;
                                double seconds = retry?.Delta?.TotalSeconds ??
                                    (retry?.Date.HasValue == true ? (retry.Date.Value - DateTimeOffset.UtcNow).TotalSeconds : attempt * 2);
                                // Never retry sooner than Retry-After; excessive delays are surfaced to the caller.
                                if (seconds > 30) throw new ApiException(status, "Upstream retry delay exceeds request budget.");
                                Detail(operation, "retry.delay", 0, (int)Math.Max(1, seconds));
                                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), cancellation).ConfigureAwait(false);
                                continue;
                            }
                            if (!response.IsSuccessStatusCode) throw new ApiException(status, "Upstream HTTP failure.");
                            if (status == 202) throw new ApiException(status, "Asynchronous upstream response requires polling support.");
                            if (status == 204 || status == 205) return "";
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            Detail(operation, "body.buffered", watch.ElapsedMilliseconds, body.Length);
                            if (string.IsNullOrWhiteSpace(body)) return "";
                            var mediaType = response.Content.Headers.ContentType?.MediaType;
                            if (!string.IsNullOrEmpty(mediaType) && mediaType != "application/json" &&
                                !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) && mediaType != "text/plain")
                                throw new ApiException(status, "Unsupported upstream content type.");
                            try { JToken.Parse(body); }
                            catch (JsonException) { throw new ApiException(status, "Upstream returned invalid JSON."); }
                            return body;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        var error = new TimeoutException("Upstream request exceeded configured timeout.");
                        Failure(operation, error);
                        throw error;
                    }
                    catch (Exception error) { Failure(operation, error); throw; }
                    finally { Detail(operation, "end", watch.ElapsedMilliseconds, attempt); }
                }
            }
        }
        public static async Task<string> AssignSecurityTokenAsync(HttpRequest req, CancellationToken cancellation)
        {
            string supplied = req.Query["token"];
            if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
            var body = JsonConvert.SerializeObject(new { username = Required("CENTRIC_USERNAME"), password = Required("CENTRIC_PASSWORD") });
            var response = await RequestAsync("session", null, body, cancellation).ConfigureAwait(false);
            var json = JToken.Parse(response);
            var token = json.Type == JTokenType.Object ? json["token"] : null;
            if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new ApiException(200, "Session response has no usable token.");
            return (string)token;
        }
        // Compatibility entry points; the Function below uses the asynchronous versions.
        public static string AssignSecurityToken(HttpRequest req) => AssignSecurityTokenAsync(req, req.HttpContext.RequestAborted).GetAwaiter().GetResult();
        public static string GetAllEndpointData(string securityToken, string endpointName, string parameters, int skip, HttpRequest req)
            => RequestAsync(endpointName + parameters + (string.IsNullOrEmpty(parameters) ? "?" : "&") + "skip=" + skip,
                securityToken, null, req.HttpContext.RequestAborted).GetAwaiter().GetResult();
        public static string GetEndpointData(string securityToken, string endpointName, string element, HttpRequest req)
            => RequestAsync(endpointName + "/" + element, securityToken, null, req.HttpContext.RequestAborted).GetAwaiter().GetResult();

        private static string ConnectionString()
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = "tcp:" + Required("DB_SERVER") + ",1433",
                InitialCatalog = Required("DB_NAME"),
                UserID = Required("DB_USER"),
                Password = Required("DB_PASSWORD"),
                PersistSecurityInfo = false,
                Encrypt = true,
                TrustServerCertificate = false,
                ConnectTimeout = Setting("CENTRIC_SQL_CONNECT_TIMEOUT_SECONDS", 30)
            }.ConnectionString;
        }
        private static string Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException("Invalid SQL identifier.");
            return "[" + value.Replace("]", "]]") + "]";
        }
        private static string Literal(string value) => value.Replace("'", "''");
        public static void ConvertAndInsert(string tableName, string tableData)
            => ConvertAndInsertAsync(tableName, tableData, CancellationToken.None).GetAwaiter().GetResult();
        public static async Task ConvertAndInsertAsync(string tableName, string tableData, CancellationToken cancellation)
        {
            using (Measure("json.to.table"))
            {
                cancellation.ThrowIfCancellationRequested();
                var parsed = JToken.Parse(tableData);
                var rows = parsed as JArray ?? (parsed is JObject ? new JArray(parsed) : null);
                if (rows == null) throw new ApiException(200, "Expected an object or array of objects.");
                if (rows.Count == 0) return;
                var table = new DataTable();
                foreach (var row in rows)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!(row is JObject)) throw new ApiException(200, "Expected object records.");
                    foreach (var property in ((JObject)row).Properties())
                        if (!table.Columns.Contains(property.Name)) table.Columns.Add(property.Name, typeof(string));
                }
                foreach (JObject row in rows)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var record = table.NewRow();
                    foreach (var property in row.Properties())
                    {
                        var v = property.Value;
                        record[property.Name] = v.Type == JTokenType.Null ? (object)DBNull.Value :
                            (v is JContainer ? (v.HasValues ? v.ToString(Formatting.None) : "") : (string)v)?.Replace("centric%3A", "");
                    }
                    table.Rows.Add(record);
                }
                if (table.Columns.Count == 0) throw new ApiException(200, "Records contain no columns.");
                await EnsureTableAsync(tableName, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName), cancellation).ConfigureAwait(false);
                await InsertAsync(tableName, table, cancellation).ConfigureAwait(false);
            }
        }
        private static async Task EnsureTableAsync(string name, IEnumerable<string> keys, CancellationToken cancellation)
        {
            var columns = keys.ToArray();
            var sql = "IF OBJECT_ID(N'dbo." + Literal(Identifier(name)) + "', N'U') IS NULL CREATE TABLE dbo." + Identifier(name) +
                " (" + string.Join(", ", columns.Select(k => Identifier(k) + " VARCHAR(2000)")) + ");";
            await ExecuteQueryAsync(sql, cancellation).ConfigureAwait(false);
            foreach (var column in columns)
                await ExecuteQueryAsync(ColumnSql(name, column), cancellation).ConfigureAwait(false);
        }
        private static string ColumnSql(string name, string column)
            => "IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME='" +
                Literal(name) + "' AND COLUMN_NAME='" + Literal(column) + "') ALTER TABLE dbo." + Identifier(name) +
                " ADD " + Identifier(column) + " VARCHAR(2000);";
        public static int UpdateTable(string tableName, string data)
        {
            var parsed = JToken.Parse(data);
            var rows = parsed as JArray ?? new JArray(parsed);
            if (rows.Count == 0) return 0;
            var keys = rows.OfType<JObject>().SelectMany(r => r.Properties()).Select(p => p.Name).Distinct();
            EnsureTableAsync(tableName, keys, CancellationToken.None).GetAwaiter().GetResult();
            return parsed is JArray ? 2 : 1;
        }
        public static Dictionary<string, string> BiggestRow(List<object> tableList)
            => tableList?.Select(r => JObject.Parse(r.ToString()).Properties().ToDictionary(p => p.Name,
                p => p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString(Formatting.None)))
                .OrderByDescending(r => r.Count).FirstOrDefault();
        public static void ExecuteQuery(string sql) => ExecuteQueryAsync(sql, CancellationToken.None).GetAwaiter().GetResult();
        private static async Task ExecuteQueryAsync(string sql, CancellationToken cancellation)
        {
            using (var connection = new SqlConnection(ConnectionString()))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = Setting("CENTRIC_SQL_COMMAND_TIMEOUT_SECONDS", 30);
                using (Measure("sql.open")) await connection.OpenAsync(cancellation).ConfigureAwait(false);
                using (Measure("sql.schema")) await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            }
        }
        public static void AddColumnsIfNotExist(string tableName, string columnName) => ExecuteQuery(ColumnSql(tableName, columnName));
        public static void InsertToTable(string tableName, DataTable tableData, bool columnTooSmall = false)
            => InsertAsync(tableName, tableData, CancellationToken.None).GetAwaiter().GetResult();
        private static async Task InsertAsync(string tableName, DataTable table, CancellationToken cancellation)
        {
            using (var connection = new SqlConnection(ConnectionString()))
            {
                using (Measure("sql.open")) await connection.OpenAsync(cancellation).ConfigureAwait(false);
                using (var bulk = new SqlBulkCopy(connection))
                {
                    bulk.BulkCopyTimeout = Setting("CENTRIC_SQL_BULK_TIMEOUT_SECONDS", 30);
                    bulk.DestinationTableName = "dbo." + Identifier(tableName);
                    foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    Detail("sql.bulk", "rows", 0, table.Rows.Count);
                    using (Measure("sql.bulk")) await bulk.WriteToServerAsync(table, cancellation).ConfigureAwait(false);
                }
            }
        }
        //Function to send out an email to the people in charge of errors to notify them of any.
        public static void SendErrorEmail(string message)
        {
            var mailMessage = new MimeMessage();
            mailMessage.From.Add(new MailboxAddress("Centric Automation", "errors@sccomputers.com"));
            mailMessage.To.Add(new MailboxAddress("Programmers", "baguiar@xobee.com"));
            mailMessage.Subject = "Centric Automated Error Message";
            mailMessage.Body = new TextPart("plain") { Text = message };
            using (var smtpClient = new SmtpClient())
            {
                smtpClient.Connect("email-smtp.us-west-1.amazonaws.com", 25, SecureSocketOptions.StartTls);
                smtpClient.Authenticate("AKIAQIOG4O6O4T5DMXKY", "BA194gJx2212I4Ra4ZjwjlJBL7FPDbds+EFytg3cM+5L");
                smtpClient.Send(mailMessage);
                smtpClient.Disconnect(true);
            }
        }
    }
}
