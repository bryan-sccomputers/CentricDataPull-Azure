using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Http;
using RestSharp;
using System.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;
using MimeKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using System.Threading;

namespace CentricDataPull
{
    public static class CentricAPI
    {
        private static string dbConnection = "Server=tcp:centrica2datapull.database.windows.net,1433;" +
                                             "Initial Catalog=CentricA2KdataPullLive;" +
                                             "Persist Security Info=False;" +
                                             "User ID=SQLSACRED;" +
                                             "Password=RHci6Te2vdvMTH;" +
                                             "MultipleActiveResultSets=False;" +
                                             "Encrypt=True;" +
                                             "TrustServerCertificate=False;" +
                                             "Connection Timeout=30;";

        //Get Centric Security Token
        public static string AssignSecurityToken(HttpRequest req)
        {
            int tries = 0;
            while (true)
            {
                try
                {
                    var tokenClient = new RestClient("https://sj-prod.centricsoftware.com/csi-requesthandler/api/v2/session");
                    tokenClient.Timeout = -1;
                    var tokenRequest = new RestRequest(Method.POST);
                    tokenRequest.AddHeader("Content-Type", "application/json");
                    var body = @"{" + "\n" +
                    @"    ""username"": ""REST_API""," + "\n" +
                    @"    ""password"": ""dd##e55?%44!!""" + "\n" +
                    @"}";
                    tokenRequest.AddParameter("application/json", body, ParameterType.RequestBody);
                    IRestResponse tokenResponse = tokenClient.Execute(tokenRequest);
                    dynamic tokenResponseData = JsonConvert.DeserializeObject(tokenResponse.Content);
                    string securityToken = req.Query["token"];
                    securityToken = securityToken ?? tokenResponseData.token;
                    return securityToken;
                }
                catch (Exception)
                {
                    if (tries < 3)
                    {
                        Thread.Sleep(5000);
                        tries++;
                        continue;
                    }
                    else
                    {
                        SendErrorEmail("Failed to get security token for authorization!");
                        return null;
                    }
                }
            }
        }

        //Pull End Point data from any table and write to screen
        public static string GetAllEndpointData(string securityToken, string endpointName, string parameters, int skip, HttpRequest req)
        {
            string skipString = (parameters != "") ? ("&skip=" + skip) : ("?skip=" + skip);
            var client = new RestClient("https://sj-prod.centricsoftware.com/csi-requesthandler/api/v2/" + endpointName + parameters + skipString);
            client.Timeout = -1;
            var request = new RestRequest(Method.GET);
            request.AddHeader("Cookie", securityToken);
            IRestResponse response = client.Execute(request);
            string responseMessage = response.Content;
            return responseMessage;
        }

        public static string GetEndpointData(string securityToken, string endpointName, string element, HttpRequest req)
        {
            var client = new RestClient("https://sj-prod.centricsoftware.com/csi-requesthandler/api/v2/" + endpointName + "/" + element);
            client.Timeout = -1;
            var request = new RestRequest(Method.GET);
            request.AddHeader("Cookie", securityToken);
            IRestResponse response = client.Execute(request);
            string responseMessage = response.Content;
            return responseMessage;
        }

        //Converts any bad Json types into strings so they can be imported into the database properly and inserts the values.
        public static void ConvertAndInsert(string tableName, string tableData)
        {
            var data = tableData.Replace("\'", "\\'").Replace("[]", "\"\"").Replace("{}", "\"\"").Replace("centric%3A", "");
            data = Regex.Replace(data, ":\\s?(\\[|\\{)(.+?)(\\}|\\])", ": '$1$2$3'");
            int rowCount = 0;
            try
            {
                rowCount = UpdateTable(tableName, data);
            }
            catch (Exception ex)
            {
                if (ex.GetType().ToString() == "Newtonsoft.Json.JsonSerializationException")
                {
                    //SendErrorEmail("Table " + tableName + " does not exist!");
                    return;
                }
            }
            try
            {
                if (rowCount == 2)
                {
                    InsertToTable(tableName, JsonConvert.DeserializeObject<DataTable>(data));
                }
                else if(rowCount == 1)
                {
                    InsertToTable(tableName, JsonConvert.DeserializeObject<DataTable>("[" + data + "]"));
                }
            }
            catch (Exception ex)
            {
                if (ex.GetType().ToString() == "Newtonsoft.Json.JsonSerializationException")
                {
                    //SendErrorEmail("Could not convert data to DataTable Object!");
                    return;
                }
            }
        }

        //Creates the datatable if it doesn't already exist.
        public static int UpdateTable(string tableName, string data)
        {
            Dictionary<string, string>.KeyCollection tableKeys;
            int isSingleRow;
            try
            {
                var tableList = JsonConvert.DeserializeObject<List<object>>(data);
                var tableRows = BiggestRow(tableList);
                if (tableRows == null)
                {
                    return 0;
                }
                else
                {
                    tableKeys = tableRows.Keys;
                    isSingleRow = 2;
                }
            }
            catch (Exception)
            {
                var tableList = JsonConvert.DeserializeObject<object>(data);
                tableKeys = JsonConvert.DeserializeObject<Dictionary<string, string>>(tableList.ToString()).Keys;
                isSingleRow = 1;
            }

            int count = 0;

            string sql = "IF NOT EXISTS (SELECT * FROM sys.objects " +
                         "WHERE object_id = OBJECT_ID(N'dbo." + tableName + "') AND type in (N'U')) " +
                         "BEGIN " +
                         "CREATE TABLE dbo." + tableName + " (";
            foreach (var key in tableKeys)
            {
                sql += count == 0 ? (key + " VARCHAR(2000)") : (", " + key + " VARCHAR(2000)");
                count++;
            }
            sql += ") END";

            ExecuteQuery(sql);
            return isSingleRow;
        }

        //Executes the stated SQL query.
        public static void ExecuteQuery(string sql)
        {
            using (SqlConnection connection = new SqlConnection(dbConnection))
            {
                using (SqlCommand command = new SqlCommand(sql, connection))
                {
                    connection.Open();
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read());
                    }
                    connection.Close();
                }
            }
        }

        //Gets the max amount of column names there are, in case certain columns are missing values. This is used when creating a datatable.
        public static Dictionary<string, string> BiggestRow(List<object> tableList)
        {
            Dictionary<string, string> biggestRow = null;
            if (tableList != null && tableList.Count > 0)
            {
                foreach (var row in tableList)
                {
                    var columns = JsonConvert.DeserializeObject<Dictionary<string, string>>(row.ToString());
                    if (biggestRow == null || columns.Count > biggestRow.Count)
                    {
                        biggestRow = columns;
                    }
                }
            }
            return biggestRow;
        }

        public static void AddColumnsIfNotExist(string tableName, string columnName)
        {
            string sql = "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS " +
                         "WHERE TABLE_NAME = '" + tableName + "' AND COLUMN_NAME = '" + columnName + "') " +
                         "BEGIN " +
                           "ALTER TABLE " + tableName + " ADD " + columnName + " VARCHAR(2000) " +
                         "END";
            ExecuteQuery(sql);
        }

        //Bulk copy to database function.
        public static void InsertToTable(string tableName, DataTable tableData, bool columnTooSmall = false)
        {
            using (SqlConnection connection = new SqlConnection(dbConnection))
            {
                connection.Open();
                using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                {
                    foreach (DataColumn c in tableData.Columns)
                    {
                        bulkCopy.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                    }
                    bulkCopy.DestinationTableName = "dbo." + tableName;
                    try
                    {
                        bulkCopy.WriteToServer(tableData);
                    }
                    //If some of the columns are too small, this will resize them and then call the function again with the correct columns
                    catch (Exception ex)
                    {
                        if (!columnTooSmall)
                        {
                            foreach (DataColumn c in tableData.Columns)
                            {
                                AddColumnsIfNotExist(tableName, c.ColumnName);
                            }
                            InsertToTable(tableName, tableData, true);
                        }
                        else
                        {
                            SendErrorEmail("Table " + tableName + ":\n" + tableData.ToString() + "\n" + ex.Message.ToString());
                        }
                    }
                }
            }
        }

        //Function to send out an email to the people in charge of errors to notify them of any.
        public static void SendErrorEmail(string message)
        {
            var mailMessage = new MimeMessage();
            mailMessage.From.Add(new MailboxAddress("Centric Automation", "errors@sccomputers.com"));
            mailMessage.To.Add(new MailboxAddress("Programmers", "jake@sccomputers.com"));
            mailMessage.To.Add(new MailboxAddress("Programmers", "danny@sccomputers.com"));
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
