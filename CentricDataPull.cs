using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CentricDataPull
{
    public static class CentricDataPull
    {
        private static string securityToken = null;
        private static DateTime expiresAt = new DateTime();

        public static string GetSecurityToken()
        {
            return securityToken;
        }
        public static void SetSecurityToken(HttpRequest req)
        {
            securityToken = CentricAPI.AssignSecurityToken(req);
        }
        public static bool SecurityTokenExists()
        {
            return GetSecurityToken() != null ? true : false;
        }
        public static void CheckSecurityToken(HttpRequest req)
        {
            if (isExpired() || !SecurityTokenExists())
            {
                SetSecurityToken(req);
                SetNextExpiration();
            }
        }
        public static DateTime GetNextExpiration()
        {
            return expiresAt;
        }
        public static void SetNextExpiration()
        {
            expiresAt = DateTime.Now.AddHours(8);
        }
        public static bool isExpired()
        {
            if (DateTime.Compare(DateTime.Now, GetNextExpiration()) > 0)
            {
                return true;
            }
            return false;
        }
        public static string UsesExtTable(string extTableName)
        {
            return (extTableName != null && extTableName != "") ? ("_" + extTableName) : "";
        }
        [FunctionName("CentricDataPullLive")]
        public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req, ILogger log)
        {
            //********************************************************************************
            // Setup for REST API calls.
            //********************************************************************************
            log.LogInformation("C# HTTP trigger function processed a request.");
            string endpointName = req.Query["name"];
            string element = req.Query["element"];
            string extTableName = req.Query["extname"];
            string parameters = req.Query["parameters"];
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            endpointName = endpointName ?? data?.name;
            element = element != null ? CentricHelper.HTMLFormatElement(element) : data?.element;
            extTableName = extTableName ?? data?.extname;

            if (parameters != null)
            {
                bool firstParam = true;
                string[] parameterArr = parameters.Trim('"').Split(',');
                parameters = "";
                foreach (var param in parameterArr)
                {
                    parameters += firstParam ? "?" + param : "&" + param;
                    firstParam = false;
                }
            }
            else
            {
                parameters = "";
            }
            //********************************************************************************

            //Get the security token for the future API calls
            CheckSecurityToken(req);
            int skip = 0;
            string currentList;
            string tableData;
            if (element == null || element == "")
            {
                bool firstList = true;
                tableData = "[";
                while ((currentList = CentricAPI.GetAllEndpointData(GetSecurityToken(), endpointName, parameters, skip, req)) != null && currentList != "[]" && currentList != "")
                {
                    tableData += (firstList ? "" : ",") + currentList.Substring(1, currentList.Length - 2);
                    firstList = false;
                    skip += 10;
                }
                tableData += "]";
            }
            else
            {
                tableData = CentricAPI.GetEndpointData(GetSecurityToken(), endpointName, element, req);
            }
            CentricAPI.ConvertAndInsert(endpointName + UsesExtTable(extTableName), tableData);

            return new OkObjectResult(tableData);
        }
    }
}
