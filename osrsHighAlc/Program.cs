using System;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;

public class Program
{
    private const string MappingApiUrl = "https://prices.runescape.wiki/api/v1/osrs/mapping";
    private const string DataApiUrl = "https://prices.runescape.wiki/api/v1/osrs/latest";
    private const int TimeSeriesEntries = 6;

    public static async Task Main()
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RuneScapeDataAnalyzer");
        var itemsData = await GetHttpContentWithRetryAsync(DataApiUrl, client);
        var mappingData = CacheManager.GetOrFetch("mappingData",
        () => GetHttpContentWithRetryAsync(MappingApiUrl, client), TimeSpan.FromHours(1));
        JObject items = JObject.Parse(itemsData);
        JArray mapping = JArray.Parse(mappingData);
        decimal natureRuneCost = GetNatureRuneCost(items);

        var extractedItems = ExtractItems(items, mapping, natureRuneCost);

        var sortedHighProfitItems = extractedItems.OrderByDescending(item => item.MaxHighHourlyProfit).ToList();
        var sortedLowProfitItems = extractedItems.OrderByDescending(item => item.ProfitPerLowItem).ToList();

        List<Item> highProfitItemsWithTradeData = new List<Item>();
        List<Item> lowProfitItemsWithTradeData = new List<Item>();

        foreach (var item in sortedHighProfitItems.Take(30))
        {
            int itemId = item.Id;
            var timeSeriesDataResponse = await GetHttpContentWithRetryAsync($"https://prices.runescape.wiki/api/v1/osrs/timeseries?timestep=5m&id={itemId}", client);
            var timeSeriesData = JObject.Parse(timeSeriesDataResponse);

            // Get the last six entries from the time series data
            var viewedEntries = timeSeriesData["data"].TakeLast(TimeSeriesEntries);

            // Initialize variables for calculating the average
            decimal totalHighPrice = 0M;
            int totalHighPriceVolume = 0;
            int validHighAvg = 0;
            foreach (var entry in viewedEntries)
            {
                int highPriceVolume = entry["highPriceVolume"]?.Value<int>() ?? 0;
                decimal avgHighPrice = 0M;
                if (entry["avgHighPrice"] != null && entry["avgHighPrice"].Type != JTokenType.Null)
                {
                    avgHighPrice = entry["avgHighPrice"].Value<decimal>();
                    validHighAvg++;
                }



                totalHighPrice += avgHighPrice;

                totalHighPriceVolume += highPriceVolume;
            }


            // If no trades in the last 30 minutes, skip this item
            if (totalHighPriceVolume == 0)
                continue;

            // Compute the weighted average prices over the last 30 minutes
            decimal averageHighPrice = 0M;
            if (totalHighPriceVolume != 0)
            {
                averageHighPrice = totalHighPrice / validHighAvg;
            }


            // Assign the values to the item object
            item.AvgHighPrice = averageHighPrice;
            item.HighPriceVolume = totalHighPriceVolume / TimeSeriesEntries; // Average over 6 time points

            highProfitItemsWithTradeData.Add(item);
        }

        foreach (var item in sortedLowProfitItems.Take(100))
        {
            int itemId = item.Id;
            var timeSeriesDataResponse = await client.GetStringAsync($"https://prices.runescape.wiki/api/v1/osrs/timeseries?timestep=1h&id={itemId}");
            var timeSeriesData = JObject.Parse(timeSeriesDataResponse);

            // Get the last six entries from the time series data
            var viewedEntries = timeSeriesData["data"].TakeLast(TimeSeriesEntries);

            // Initialize variables for calculating the average
            decimal totalLowPrice = 0M;
            int totalLowPriceVolume = 0;
            int validLowAvg = 0;
            foreach (var entry in viewedEntries)
            {
                int lowPriceVolume = entry["lowPriceVolume"]?.Value<int>() ?? 0;



                decimal avgLowPrice = 0M;
                if (entry["avgLowPrice"] != null && entry["avgLowPrice"].Type != JTokenType.Null)
                {
                    avgLowPrice = entry["avgLowPrice"].Value<decimal>();
                    validLowAvg++;
                }


                totalLowPrice += avgLowPrice;


                totalLowPriceVolume += lowPriceVolume;
            }


            // If no trades in the last 30 minutes, skip this item
            if (totalLowPriceVolume == 0)
                continue;

            // Compute the weighted average prices over the last 30 minutes

            decimal averageLowPrice = 0M;
            if (totalLowPriceVolume != 0)
            {
                averageLowPrice = totalLowPrice / validLowAvg;
            }

            // Assign the values to the item object
            item.AvgLowPrice = averageLowPrice;
            item.LowPriceVolume = totalLowPriceVolume / TimeSeriesEntries;   // Average over 6 time points

            lowProfitItemsWithTradeData.Add(item);
        }



        var itemsForHighDisplay = highProfitItemsWithTradeData.Where(item => item.HighPriceVolume > 100).ToList();
        var itemsForLowDisplay = lowProfitItemsWithTradeData.Where(item => item.LowPriceVolume > 6).ToList();


        // Display the items
        DisplayHighItems(itemsForHighDisplay);
        DisplayLowItems(itemsForLowDisplay);
    }

    private static async Task<string> GetHttpContentWithRetryAsync(string url, HttpClient client, int maxRetries = 3)
    {
        int retryCount = 0;
        while (true)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }
                else
                {
                    // Log non-success status code
                    Console.WriteLine($"Request to {url} failed with status code {response.StatusCode}.");
                    if ((int)response.StatusCode >= 500 && retryCount < maxRetries)
                    {
                        // If it's a server error, we can retry
                        retryCount++;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount))); // Exponential backoff
                        continue;
                    }
                    else
                    {
                        // If it's not a server error or we've exhausted retries, throw an exception
                        throw new HttpRequestException($"Request to {url} failed with status code {response.StatusCode}.");
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"An exception occurred when requesting {url}: {ex.Message}");
                if (retryCount < maxRetries)
                {
                    // Retry on transient errors
                    retryCount++;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount))); // Exponential backoff
                    continue;
                }
                else
                {
                    // If we've exhausted retries, rethrow the exception
                    throw;
                }
            }
        }
    }

    private static decimal GetNatureRuneCost(JObject items)
    {
        try
        {
            return items["data"]["561"]["high"].Value<decimal>();
        }
        catch
        {
            return 0M;
        }
    }
    private static List<Item> ExtractItems(JObject items, JArray mapping, decimal natureRuneCost)
    {
        return items["data"]
            .Children<JProperty>()
            .Select(item => ExtractItemDetails(item))
            .Join(mapping, item => item.Id, mapItem => mapItem["id"].Value<int>(), (item, mapItem) => CombineItemAndMapping(item, mapItem, natureRuneCost))
            .Where(item => item.Limit > 0 && (item.ProfitPerHighItem > 0 || item.ProfitPerLowItem > 0))
            
            .ToList();
    }

    private static Item ExtractItemDetails(JProperty item)
    {
        decimal highValue = item.Value["high"]?.Value<decimal?>() ?? 0M;
        decimal lowValue = item.Value["low"]?.Value<decimal?>() ?? 0M;
        long highTime = item.Value["highTime"]?.Value<long?>() ?? 0;
        long lowTime = item.Value["lowTime"]?.Value<long?>() ?? 0;

        return new Item
        {
            Id = int.Parse(item.Name),
            High = highValue,
            Low = lowValue,
            HighPriceVolume = item.Value["highPriceVolume"]?.Value<int>() ?? 0,
            LowPriceVolume = item.Value["lowPriceVolume"]?.Value<int>() ?? 0
        };
    }

    private static Item CombineItemAndMapping(Item item, JToken mapItem, decimal natureRuneCost)
    {
        item.Name = mapItem["name"].ToString();
        item.HighAlch = mapItem["highalch"]?.Value<decimal>() ?? 0;
        item.Limit = Math.Min(mapItem["limit"]?.Value<int>() ?? 0, 1200);
        

        // Calculate profit for High and Low
        item.ProfitPerHighItem = item.HighAlch - item.High - natureRuneCost;
        item.ProfitPerLowItem = item.HighAlch - item.Low - natureRuneCost;

        item.MaxHighHourlyProfit = item.ProfitPerHighItem * item.Limit;
        item.MaxLowHourlyProfit = item.ProfitPerLowItem * item.Limit;

        return item;
    }


    private static void DisplayHighItems(List<Item> extractedItems)
    {
        Console.WriteLine("\nTop High Items: 30min");
        Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------------------------------------------------------");
        Console.WriteLine($"{"ID",-10} | {"Name",-30} | {"High Value",-15} | {"High Avg",-15} | {"High Alch",-15} | {"Profit/Item",-15} | {"Profit\\hr",-20} | {"High Volume",-15} ");
        Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------------------------------------------------------");

        foreach (var item in extractedItems.Take(10))
        {
            Console.WriteLine($"{item.Id,-10} | {item.Name,-30} | {item.High,-15:N2} | {item.AvgHighPrice,-15:N2} | {item.HighAlch,-15:N2} | {item.ProfitPerHighItem,-15:N2} | {item.MaxHighHourlyProfit,-20:N2} | {item.HighPriceVolume,-15}");
        }

        Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------------------------------------------------------");
    }

    private static void DisplayLowItems(List<Item> extractedItems)
    {
        // Sort based on potential hourly low profit
 

        Console.WriteLine("\nTop Low Items:  6h");
        Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------------------------------------------------------");
        Console.WriteLine($"{"ID",-10} | {"Name",-30} | {"Low Value",-15}| {"Low avg",-15} | {"High Alch",-15} | {"Profit/Item",-15} | {"Profit\\hr",-20} | {"Low Volume",-15}");
        Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------------------------------------------------------");

        foreach (var item in extractedItems.Take(20))
        {

            Console.WriteLine($"{item.Id,-10} | {item.Name,-30}| {item.Low,-15:N2} | {item.AvgLowPrice,-15:N2} | {item.HighAlch,-15:N2} | {item.ProfitPerLowItem,-15:N2} | {item.MaxLowHourlyProfit,-20:N2} | {item.LowPriceVolume,-15}");

        }

        Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------------------------------------------------------");
    }



public class CachedData<T>
{
    public T Data { get; set; }
    public DateTime Expiry { get; set; }
}

public class CacheManager
{
    private static ConcurrentDictionary<string, CachedData<object>> _cache = new();

    public static T GetOrFetch<T>(string key, Func<Task<T>> fetch, TimeSpan duration)
    {
        if (_cache.TryGetValue(key, out CachedData<object> cachedData))
        {
            if (DateTime.UtcNow < cachedData.Expiry)
            {
                return (T)cachedData.Data;
            }
        }

        // Data is either not in cache or expired, so fetch and update cache
        var data = fetch().GetAwaiter().GetResult();
        _cache[key] = new CachedData<object> { Data = data, Expiry = DateTime.UtcNow.Add(duration) };
        return data;
    }

    // Call this method to clear expired items from the cache periodically if necessary
    public static void Cleanup()
    {
        var keysToRemove = _cache.Where(kvp => DateTime.UtcNow >= kvp.Value.Expiry).Select(kvp => kvp.Key).ToList();
        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }
}

private class Item
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public int Limit { get; set; }
        public decimal HighAlch { get; set; }
        public decimal ProfitPerHighItem { get; set; }
        public decimal ProfitPerLowItem { get; set; }
        public decimal AvgHighPrice { get; set; }
        public decimal AvgLowPrice { get; set; }
        public int HighPriceVolume { get; set; }
        public int LowPriceVolume { get; set; }
        public decimal MaxHighHourlyProfit {get; set; }
        public decimal MaxLowHourlyProfit { get; set; }
    }
}
