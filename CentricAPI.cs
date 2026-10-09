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
            ServerCertificateCustomValidationCallback =
    (request, certificate, chain, errors) =>
    {
        Info("TLS certificate validation Errors={Errors}", errors);

        if (chain != null)
        {
            foreach (var item in chain.ChainElements)
            {
                Info(
                    "TLS certificate Subject={Subject} Issuer={Issuer} Thumbprint={Thumbprint}",
                    item.Certificate.Subject,
                    item.Certificate.Issuer,
                    item.Certificate.Thumbprint);

                foreach (var status in item.ChainElementStatus)
                {
                    Info(
                        "TLS chain Status={Status} Detail={Detail}",
                        status.Status,
                        status.StatusInformation.Trim());
                }
            }
        }

        // Preserve certificate validation.
        return errors == System.Net.Security.SslPolicyErrors.None;
    },
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
            Logger.Value?.LogError("Centric Operation={Operation} ErrorType={ErrorType} Category={Category} Stack={Stack} InnerType={InnerType}",
                operation, error.GetType().Name,
                error is ApiException || (error is InvalidOperationException && error.Message.StartsWith("Missing configuration:"))
                    ? error.Message : "See error type, stack and provider codes",
                error.StackTrace, error.InnerException?.GetType().Name);

            var sql = error as SqlException;
            if (sql != null)
            {
                foreach (SqlError item in sql.Errors)
                {
                    Logger.Value?.LogError(
                        "SQL failure Number={Number} State={State} Class={Class} " +
                        "Procedure={Procedure} Line={Line} ConnectionId={ConnectionId} " +
                        "Message={Message}",
                        item.Number,
                        item.State,
                        item.Class,
                        item.Procedure,
                        item.LineNumber,
                        sql.ClientConnectionId,
                        item.Message);
                }
            }
        }
        public static void Info(string message, params object[] values) => Logger.Value?.LogInformation(message, values);
        private static string SafeUrl(Uri uri)
        {
            // Preserve pagination and integration flags; redact all other query values and element IDs.
            var segments = uri.AbsolutePath.Split('/');
            var rootSegments = ApiUri("").AbsolutePath.Split('/').Length;
            for (int i = rootSegments; i < segments.Length; i++) segments[i] = "[redacted]";
            var query = uri.Query.TrimStart('?').Split('&').Where(x => x.Length > 0).Select(x =>
            {
                var pair = x.Split(new[] { '=' }, 2);
                var key = Uri.UnescapeDataString(pair[0]);
                var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "";
                bool safe = key.Equals("skip", StringComparison.OrdinalIgnoreCase) && value.All(char.IsDigit)
                    || (key == "spi_ready_for_integration" || key == "spi_erp_processed")
                        && (value == "true" || value == "false");
                return Uri.EscapeDataString(key) + "=" + (safe ? value : "[redacted]");
            });
            return uri.GetLeftPart(UriPartial.Authority) + "/" + string.Join("/", segments.Skip(1))
                + (uri.Query.Length == 0 ? "" : "?" + string.Join("&", query));
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
                    Info("HTTP start Method={Method} Url={Url} Attempt={Attempt} MaxAttempts={MaxAttempts} TimeoutSeconds={TimeoutSeconds}",
                        request.Method.Method, SafeUrl(request.RequestUri), attempt, attempts, Setting("CENTRIC_HTTP_TIMEOUT_SECONDS", 30));
                    try
                    {
                        using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                        {
                            int status = (int)response.StatusCode;
                            Detail(operation, "response.status", watch.ElapsedMilliseconds, status);
                            Info("HTTP response Url={Url} Attempt={Attempt} Status={Status} ElapsedMs={ElapsedMs} ContentType={ContentType} ContentLength={ContentLength}",
                                SafeUrl(request.RequestUri), attempt, status, watch.ElapsedMilliseconds,
                                response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentLength);
                            bool transient = status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
                            if (transient && attempt < attempts)
                            {
                                var retry = response.Headers.RetryAfter;
                                double seconds = retry?.Delta?.TotalSeconds ??
                                    (retry?.Date.HasValue == true ? (retry.Date.Value - DateTimeOffset.UtcNow).TotalSeconds : attempt * 2);
                                // Never retry sooner than Retry-After; excessive delays are surfaced to the caller.
                                if (seconds > 30) throw new ApiException(status, "Upstream retry delay exceeds request budget.");
                                Info("HTTP retry Url={Url} Status={Status} NextAttempt={NextAttempt} DelaySeconds={DelaySeconds}",
                                    SafeUrl(request.RequestUri), status, attempt + 1, Math.Max(1, seconds));
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
                    catch (Exception error)
                    {
                        Info(
                            "HTTP failed Url={Url} Attempt={Attempt} ElapsedMs={ElapsedMs}",
                            SafeUrl(request.RequestUri),
                            attempt,
                            watch.ElapsedMilliseconds);

                        if (error.InnerException is
                            System.Security.Authentication.AuthenticationException tlsError)
                        {
                            Info("TLS diagnostic Message={Message}", tlsError.Message);

                            if (tlsError.InnerException != null)
                            {
                                Info(
                                    "TLS underlying Type={Type} Message={Message}",
                                    tlsError.InnerException.GetType().Name,
                                    tlsError.InnerException.Message);
                            }
                        }

                        Failure(operation, error);
                        throw;
                    }
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
                if (rows.Count == 0) { Info("SQL skipped Table={Table} Reason=empty_result", tableName); return; }
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
                foreach (DataColumn column in table.Columns)
                {
                    int maxLength = table.Rows.Cast<DataRow>().Where(r => !r.IsNull(column))
                        .Select(r => ((string)r[column]).Length).DefaultIfEmpty(0).Max();
                    if (maxLength > 2000)
                        Logger.Value?.LogWarning("SQL value may exceed generated VARCHAR(2000) Table={Table} Column={Column} MaxCharacters={MaxCharacters}", tableName, column.ColumnName, maxLength);
                }
                /*
                Info("SQL conversion complete Table={Table} Rows={Rows} Columns={Columns}", tableName, table.Rows.Count, table.Columns.Count);
                if (string.Equals(
    tableName,
    "collections_styles",
    StringComparison.OrdinalIgnoreCase))
                {
                    Info(
    "SQL insert preview Table={Table} Rows={Rows} Columns={Columns}",
    tableName,
    table.Rows.Count,
    table.Columns.Count);

                    const int chunkSize = 2000;

                    for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                    {
                        DataRow row = table.Rows[rowIndex];

                        string recordId = table.Columns.Contains("id")
                            && !row.IsNull("id")
                                ? Convert.ToString(row["id"])
                                : "(unknown)";

                        foreach (DataColumn column in table.Columns)
                        {
                            bool isNull = row.IsNull(column);
                            string value = isNull ? null : Convert.ToString(row[column]);

                            Info(
                                "SQL insert column Table={Table} Row={Row} RecordId={RecordId} " +
                                "Column={Column} DataType={DataType} IsNull={IsNull} Characters={Characters}",
                                tableName,
                                rowIndex,
                                recordId,
                                column.ColumnName,
                                column.DataType.Name,
                                isNull,
                                value?.Length ?? 0);

                            if (isNull)
                                continue;

                            int chunks = Math.Max(
                                1,
                                (int)Math.Ceiling(value.Length / (double)chunkSize));

                            for (int chunk = 0; chunk < chunks; chunk++)
                            {
                                int start = chunk * chunkSize;
                                int length = Math.Min(chunkSize, value.Length - start);

                                Info(
                                    "SQL insert value Table={Table} Row={Row} RecordId={RecordId} " +
                                    "Column={Column} Chunk={Chunk} TotalChunks={TotalChunks} Value={Value}",
                                    tableName,
                                    rowIndex,
                                    recordId,
                                    column.ColumnName,
                                    chunk + 1,
                                    chunks,
                                    value.Substring(start, length));
                            }
                        }
                    }
                }
                */
                await EnsureTableAsync(tableName, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName), cancellation).ConfigureAwait(false);
                await InsertAsync(tableName, table, cancellation).ConfigureAwait(false);
            }
        }
        private static async Task EnsureTableAsync(string name, IEnumerable<string> keys, CancellationToken cancellation)
        {
            var columns = keys.ToArray();
            Info("SQL schema start Table={Table} Columns={Columns}", name, columns.Length);
            var sql = "IF OBJECT_ID(N'dbo." + Literal(Identifier(name)) + "', N'U') IS NULL CREATE TABLE dbo." + Identifier(name) +
                " (" + string.Join(", ", columns.Select(k => Identifier(k) + " VARCHAR(2000)")) + ");";
            await ExecuteQueryAsync(sql, cancellation).ConfigureAwait(false);
            foreach (var column in columns)
            {
                Detail("sql.schema", "column.check");
                Info("SQL column check Table={Table} Column={Column}", name, column);
                await ExecuteQueryAsync(ColumnSql(name, column), cancellation).ConfigureAwait(false);
            }
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
                    Info("SQL bulk start Table={Table} Rows={Rows} Columns={Columns} TimeoutSeconds={TimeoutSeconds}", tableName, table.Rows.Count, table.Columns.Count, bulk.BulkCopyTimeout);
                    bulk.DestinationTableName = "dbo." + Identifier(tableName);
                    foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    Detail("sql.bulk", "rows", 0, table.Rows.Count);
                    using (Measure("sql.bulk")) await bulk.WriteToServerAsync(table, cancellation).ConfigureAwait(false);
                    Info("SQL bulk completed Table={Table} Rows={Rows}", tableName, table.Rows.Count);
                }
            }
        }
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
