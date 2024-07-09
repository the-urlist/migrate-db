using Microsoft.Extensions.Configuration; // Add this using directive // Add this using directive
using Microsoft.Azure.Cosmos;
using TheUrlist.Migration.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheUrlist.Migration;

class Program
{
    static async Task Main(string[] args)
    {
        // Setup custom serializer to use System.Text.Json
        JsonSerializerOptions jsonSerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        CosmosSystemTextJsonSerializer cosmosSystemTextJsonSerializer = new(jsonSerializerOptions);
        CosmosClientOptions cosmosClientOptions = new()
        {
            ApplicationName = "SystemTextJson",
            Serializer = cosmosSystemTextJsonSerializer
        };

        var builder = new ConfigurationBuilder()
             .SetBasePath(Directory.GetCurrentDirectory())
             .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        IConfigurationRoot configuration = builder.Build();

        var endpoint = configuration["AppSettings:COSMOSDB_ENDPOINT"] ?? "";
        var key = configuration["AppSettings:COSMOSDB_KEY"] ?? "";
        var databaseName = configuration["AppSettings:COSMOSDB_DATABASE"] ?? "";

        var hasherKey = configuration["AppSettings:HASHER_KEY"] ?? "";
        var hasherSalt = configuration["AppSettings:HASHER_SALT"] ?? "";

        var hasher = new Hasher(hasherKey, hasherSalt);

        // Connect to a Cosmos DB database
        var client = new CosmosClient(endpoint, key);

        // Connect the database
        var database = client.GetDatabase(databaseName);

        // find every record where the userId is not null and there is no identityProvider field
        var container = database.GetContainer("linkbundles");

        var query = container.GetItemQueryIterator<LinkBundle>("SELECT * FROM c WHERE c.userId != '' AND NOT IS_DEFINED(c.identityProvider)");

        // get a count of all results
        var count = 0;
        while (query.HasMoreResults)
        {
            try
            {
                var response = await query.ReadNextAsync();
                count += response.Count;

                foreach (var item in response)
                {
                    var identityProvider = "twitter";
                    var userId = hasher.HashString(item.UserId);
                    var partitionKey = new PartitionKey(item.VanityUrl);

                    List<PatchOperation> operations = new()
                    {
                        PatchOperation.Add("/provider", identityProvider),
                        PatchOperation.Set("/userId", userId)
                    };

                    Console.WriteLine($"Processing record for id {item.Id}");

                    try
                    {
                        var result = await container.PatchItemAsync<LinkBundle>(
                            id: item.Id,
                            partitionKey: partitionKey,
                            patchOperations: operations);
                        Console.WriteLine($"Updated record for user {item.UserId}");
                    }
                    catch (Microsoft.Azure.Cosmos.CosmosException ex)
                    {
                        Console.WriteLine($"Error updating item {item.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while processing the results: {ex.Message}");
                break; // Or handle the error as appropriate for your scenario
            }
        }
    }
}