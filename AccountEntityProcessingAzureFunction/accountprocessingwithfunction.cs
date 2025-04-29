using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Azure.Storage.Blobs;
using CsvHelper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net;
using DotNetEnv;

namespace DynamicsAccountProcessor
{
    public static class ProcessAccountsFunction
    {
        // Static constructor to load environment variables
        static ProcessAccountsFunction()
        {
            Env.Load(); // Loads .env file from root
        }

        // Read from environment
        private static readonly string clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
        private static readonly string clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
        private static readonly string tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
        private static readonly string dynamicsUrl = Environment.GetEnvironmentVariable("DYNAMICS_URL");
        private static readonly string blobConnectionString = Environment.GetEnvironmentVariable("BLOB_CONNECTION_STRING");
        private static readonly string blobContainerName = Environment.GetEnvironmentVariable("BLOB_CONTAINER_NAME");

        [Function("ProcessAccountsHttp")]
        public static async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("ProcessAccountsHttp");
            var startTime = DateTime.UtcNow;
            logger.LogInformation($"Processing started at {startTime}");

            var response = req.CreateResponse();

            try
            {
                var token = await GetAccessToken();
                var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                client.DefaultRequestHeaders.Add("Prefer", "odata.maxpagesize=5000");

                var accounts = await FetchAllAccounts(client, logger);
                var csvPath = await GenerateCsv(accounts, logger);
                await UploadToBlob(csvPath, logger);

                foreach (var account in accounts)
                {
                    if (account.Processed == "No")
                    {
                        await UpdateProcessedStatusSequential(account.Id, client, logger);
                    }
                }

                var endTime = DateTime.UtcNow;
                var timeTaken = endTime - startTime;
                logger.LogInformation($"Processing completed at {endTime}, Total time taken: {timeTaken}");

                response.StatusCode = HttpStatusCode.OK;
                await response.WriteStringAsync($"Successfully processed {accounts.Count} records. Time Taken: {timeTaken}");
            }
            catch (Exception ex)
            {
                logger.LogError($"Error during processing: {ex.Message}");
                response.StatusCode = HttpStatusCode.InternalServerError;
                await response.WriteStringAsync($"Error: {ex.Message}");
            }

            return response;
        }

        private static async Task<string> GetAccessToken()
        {
            var app = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenantId}"))
                .Build();

            var scopes = new string[] { $"{dynamicsUrl}/.default" };
            var authResult = await app.AcquireTokenForClient(scopes).ExecuteAsync();
            return authResult.AccessToken;
        }

        private static async Task<List<AccountRecord>> FetchAllAccounts(HttpClient client, ILogger logger)
        {
            var selectFields = "name,telephone1,fax,websiteurl,address1_composite,revenue,numberofemployees," +
                               "preferredcontactmethodcode,industrycode,sic,address1_longitude,address1_latitude," +
                               "customertypecode,cr356_duedate,cr356_processed";
            var url = $"{dynamicsUrl}/api/data/v9.2/accounts?$select={selectFields}";
            var accounts = new List<AccountRecord>();

            while (!string.IsNullOrEmpty(url))
            {
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) throw new Exception($"Error fetching data: {response.StatusCode}");

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);
                var values = (JArray)json["value"];

                foreach (var item in values)
                {
                    var account = new AccountRecord
                    {
                        Id = item["accountid"]?.ToString(),
                        Name = item["name"]?.ToString(),
                        Telephone = item["telephone1"]?.ToString(),
                        Fax = item["fax"]?.ToString(),
                        Website = item["websiteurl"]?.ToString(),
                        Address = item["address1_composite"]?.ToString(),
                        Revenue = item["revenue"]?.ToObject<decimal?>(),
                        NumberOfEmployees = item["numberofemployees"]?.ToObject<int?>(),
                        PreferredContactMethod = item["preferredcontactmethodcode"]?.ToString(),
                        Industry = item["industrycode"]?.ToString(),
                        SIC = item["sic"]?.ToString(),
                        Longitude = item["address1_longitude"]?.ToObject<double?>(),
                        Latitude = item["address1_latitude"]?.ToObject<double?>(),
                        RelationshipType = item["customertypecode"]?.ToString(),
                        DueDate = item["cr356_duedate"]?.ToObject<DateTime?>(),
                        Processed = item["cr356_processed"]?.ToString() ?? "No"
                    };
                    accounts.Add(account);
                }

                url = json["@odata.nextLink"]?.ToString();
            }

            logger.LogInformation($"Fetched {accounts.Count} accounts.");
            return accounts;
        }

        private static async Task UpdateProcessedStatusSequential(string accountId, HttpClient client, ILogger logger, int retryCount = 3)
        {
            var url = $"{dynamicsUrl}/api/data/v9.2/accounts({accountId})";
            var content = new StringContent("{\"cr356_processed\":\"TRUE\"}", Encoding.UTF8, "application/json");

            int attempt = 0;
            while (attempt < retryCount)
            {
                var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
                var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation($"Successfully updated Processed field for account {accountId}.");
                    return;
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                logger.LogError($"Failed to update account {accountId}. Status: {response.StatusCode}, Error: {errorContent}");

                attempt++;
                if (attempt < retryCount)
                {
                    logger.LogWarning($"Retrying {accountId}, attempt {attempt}/{retryCount}...");
                    await Task.Delay(2000);
                }
                else
                {
                    logger.LogError($"Max retries reached for account {accountId}. Skipping.");
                }
            }
        }

        private static async Task<string> GenerateCsv(List<AccountRecord> accounts, ILogger logger)
        {
            var path = Path.GetTempFileName();

            using (var writer = new StreamWriter(path))
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                await csv.WriteRecordsAsync(accounts);
            }

            logger.LogInformation($"CSV file generated at {path}");
            return path;
        }

        private static async Task UploadToBlob(string filePath, ILogger logger)
        {
            var blobServiceClient = new BlobServiceClient(blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(blobContainerName);
            var blobClient = containerClient.GetBlobClient($"accountentityprocessing/{DateTime.UtcNow:yyyy/MM/dd}/AccountsProcessed_{DateTime.UtcNow:HHmmss}.csv");

            using (var fileStream = File.OpenRead(filePath))
            {
                await blobClient.UploadAsync(fileStream, true);
            }

            logger.LogInformation("CSV file uploaded to Blob Storage.");
        }

        public class AccountRecord
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Telephone { get; set; }
            public string Fax { get; set; }
            public string Website { get; set; }
            public string Address { get; set; }
            public decimal? Revenue { get; set; }
            public int? NumberOfEmployees { get; set; }
            public string PreferredContactMethod { get; set; }
            public string Industry { get; set; }
            public string SIC { get; set; }
            public double? Longitude { get; set; }
            public double? Latitude { get; set; }
            public string RelationshipType { get; set; }
            public DateTime? DueDate { get; set; }
            public string Processed { get; set; }
        }
    }
}