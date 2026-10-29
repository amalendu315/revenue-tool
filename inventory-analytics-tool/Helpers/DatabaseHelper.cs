using inventory_analytics_tool.Models;
using Microsoft.Data.SqlClient; // Ensure you have this NuGet package installed
using Microsoft.Extensions.Configuration;
using System;
using System.Data;
using System.Threading.Tasks;

namespace inventory_analytics_tool.Helpers
{
    public interface IDatabaseHelper
    {
        Task<int> GetAttemptCountAsync(string sector, DateTime startDate, DateTime endDate);
        Task<List<AttemptRecord>> GetAttemptDetailsAsync(string sector, DateTime startDate, DateTime endDate);
        Task<List<FareTrendRecord>> GetFareTrendAsync(string sector, DateTime departureDate, string airline, string flightNo);
        Task<Dictionary<DateTime, (double LowestFare, DateTime EntryDate, string Airline, string FlightNo)>> GetLowestLatestFaresAsync(string sector, DateTime startDate, DateTime endDate);
        Task<List<FlightFareRecord>> GetFlightLatestFaresAsync(string sector, DateTime travelDate);
        Task<int?> GetLatestBlackoutFareFeedIdAsync(string sector, DateTime departureDate, string airline, string flightNo);
        Task<bool> BulkInsertGroupRaisedEntriesAsync(List<GroupRaisedEntryModel> entries);
        Task<List<AttemptRecord>> GetAllAttemptDetailsAsync(DateTime? entryStartDate, DateTime? entryEndDate, DateTime? travelDate);
        Task<int> GetOrInsertBlackoutFareFeedIdAsync(string sector, DateTime departureDate, string airline, string flightNo, decimal amount, string arrivalTime, string departureTime);
        Task<List<string>> GetUserSectorAlertPreferencesAsync(string userName);
        Task<bool> SaveUserSectorAlertPreferencesAsync(string userName, List<string> sectors);
        Task<Dictionary<string, int>> GetBulkAttemptCountsBySectorAsync(List<string> sectors, DateTime startDate, DateTime endDate);
        Task<Dictionary<DateTime, int>> GetBulkAttemptCountsByDateAsync(string sector, DateTime startDate, DateTime endDate);
        Task<List<FlightFareRecord>> GetBulkFlightLatestFaresAsync(string sector, DateTime startDate, DateTime endDate);
        Task<List<(string Sector, DateTime TravelDate, int Count)>> GetBulkAttemptCountsDetailedAsync(List<string> sectors, DateTime startDate, DateTime endDate);
        Task<List<FareAuditDbRecord>> GetRecentFareAuditLogsAsync();
        Task<bool> CreateUserSessionAsync(UserSession session);
        Task<bool> CloseUserSessionAsync(Guid sessionId, DateTime logoutTime);
        Task<bool> InsertUserActivityLogAsync(UserActivityLog log);
        Task<bool> InsertUserUploadLogAsync(UserUploadLog log);
        Task<IEnumerable<UserSession>> GetUserSessionsAsync();
        Task<IEnumerable<UserActivityLog>> GetUserActivityLogsAsync();
        Task<IEnumerable<UserUploadLog>> GetUserUploadLogsAsync();
        Task<Dictionary<string, (DateTime TravelDate, double LatestFare, double Avg7DayFare, int FlightCount)>> GetBulkDemandFareStatsAsync(string sectorCsv, DateTime startDate, DateTime endDate);
    }

    public class DatabaseHelper : IDatabaseHelper
    {
        private readonly string _connectionString;

        public DatabaseHelper(IConfiguration configuration)
        {
            // It will securely look for "DefaultConnection" in appsettings.json
            _connectionString = configuration.GetConnectionString("DefaultConnection")!;
        }

        public async Task<int> GetAttemptCountAsync(string sector, DateTime startDate, DateTime endDate)
        {
            int count = 0;
            if (string.IsNullOrEmpty(_connectionString)) return 0;

            using (var connection = new SqlConnection(_connectionString))
            {
                string query = @"
                    SELECT Count(GR.[GroupRaisedEntryID])
                    FROM [Blackout].[GroupRaisedEntry] As GR
                    LEFT JOIN Blackout.BlackoutFareFeed As FF ON GR.BlackoutFareFeedID = FF.BlackoutFareFeedID
                    WHERE FF.Sector = @Sector 
                      AND FF.[DepartureDate] >= @StartDate 
                      AND FF.[DepartureDate] <= @EndDate";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Sector", sector);
                    command.Parameters.AddWithValue("@StartDate", startDate);
                    command.Parameters.AddWithValue("@EndDate", endDate);

                    try
                    {
                        await connection.OpenAsync();
                        var result = await command.ExecuteScalarAsync();
                        if (result != null && result != DBNull.Value)
                        {
                            count = Convert.ToInt32(result);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fails gracefully to 0 so it doesn't crash the dashboard if the DB is down
                        Console.WriteLine($"DB Fetch Error: {ex.Message}");
                    }
                }
            }
            return count;
        }

        public async Task<List<AttemptRecord>> GetAttemptDetailsAsync(string sector, DateTime startDate, DateTime endDate)
        {
            var results = new List<AttemptRecord>();
            if (string.IsNullOrEmpty(_connectionString)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                string query = @"
                    SELECT 
                        GR.[GroupRaisedEntryID], GR.[GroupRaisedRefNo], GR.[RaisedPax], 
                        GR.[GroupFare], GR.[Status], GR.[NegotiatedFare], GR.[Remarks], GR.[OpenRemarks], GR.[ConfirmationStatus],
                        GR.[PNR], GR.[UserName], GR.[EntryDate],
                        FF.[DepartureDate], FF.[Airline], FF.[FlightNo], FF.[Sector], FF.[Fare]
                    FROM [Blackout].[GroupRaisedEntry] AS GR
                    LEFT JOIN [Blackout].[BlackoutFareFeed] AS FF 
                        ON GR.[BlackoutFareFeedID] = FF.[BlackoutFareFeedID]
                    WHERE FF.[Sector] = @Sector 
                      AND FF.[DepartureDate] >= @StartDate 
                      AND FF.[DepartureDate] < DATEADD(day, 1, @EndDate)";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Sector", sector);
                    command.Parameters.AddWithValue("@StartDate", startDate.Date);
                    command.Parameters.AddWithValue("@EndDate", endDate.Date);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {

                                string dbSector = reader["Sector"]?.ToString() ?? "";
                                var parts = dbSector.Split('-');
                                string origin = parts.Length > 0 ? parts[0] : "";
                                string destination = parts.Length > 1 ? parts[1] : "";

                                DateTime departureDate = reader["DepartureDate"] != DBNull.Value ? Convert.ToDateTime(reader["DepartureDate"]) : DateTime.MinValue;
                                DateTime entryDate = reader["EntryDate"] != DBNull.Value ? Convert.ToDateTime(reader["EntryDate"]) : DateTime.MinValue;

                                string airline = reader["Airline"]?.ToString() ?? "";
                                string flightNo = reader["FlightNo"]?.ToString() ?? "";

                                results.Add(new AttemptRecord
                                {
                                    Id = reader["GroupRaisedEntryID"] != DBNull.Value ? Convert.ToInt32(reader["GroupRaisedEntryID"]) : 0,
                                    AqId = "AQ" + (reader["GroupRaisedEntryID"]?.ToString() ?? ""),
                                    ReqId = reader["GroupRaisedRefNo"]?.ToString() ?? "",
                                    Origin = origin,
                                    Destination = destination,
                                    TravelDate = departureDate.ToString("dd MMM yyyy"),
                                    RawTravelDate = departureDate,
                                    EntryDate = entryDate.ToString("dd MMM yyyy"),
                                    RawEntryDate = entryDate,
                                    AirlineCode = airline,
                                    FlightCode = airline + " " + flightNo,
                                    Pax = reader["RaisedPax"] != DBNull.Value ? Convert.ToInt32(reader["RaisedPax"]) : 0,
                                    AutoQuote = reader["GroupFare"] != DBNull.Value ? "₹" + reader["GroupFare"].ToString() : "0",
                                    OnlineFare = reader["Fare"] != DBNull.Value ? "₹" + Convert.ToDouble(reader["Fare"]).ToString("N0") : "0",
                                    Status = reader["Status"]?.ToString() ?? "",
                                    NegFare = reader["NegotiatedFare"] != DBNull.Value ? "₹" + reader["NegotiatedFare"].ToString() : "0",
                                    ConfStatus = reader["ConfirmationStatus"]?.ToString() ?? "",
                                    Remarks = reader["Remarks"]?.ToString() ?? "",
                                    PNR = reader["PNR"]?.ToString() ?? "-",
                                    OpenRemarks = reader["OpenRemarks"]?.ToString() ?? "-",
                                    Lead = reader["UserName"]?.ToString() ?? ""
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DB Details Fetch Error: {ex.Message}");
                    }
                }
            }
            return results;
        }

        public async Task<List<AttemptRecord>> GetAllAttemptDetailsAsync(DateTime? entryStartDate, DateTime? entryEndDate, DateTime? travelDate)
        {
            var results = new List<AttemptRecord>();
            if (string.IsNullOrEmpty(_connectionString)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                string query = @"
                    SELECT 
                        GR.[GroupRaisedEntryID], GR.[GroupRaisedRefNo], GR.[RaisedPax], 
                        GR.[GroupFare], GR.[Status], GR.[NegotiatedFare], GR.[Remarks], GR.[OpenRemarks], GR.[ConfirmationStatus],
                        GR.[PNR], GR.[UserName], GR.[EntryDate], 
                        FF.[DepartureDate], FF.[Airline], FF.[FlightNo], FF.[Sector], FF.[Fare]
                    FROM [Blackout].[GroupRaisedEntry] AS GR
                    LEFT JOIN [Blackout].[BlackoutFareFeed] AS FF 
                        ON GR.[BlackoutFareFeedID] = FF.[BlackoutFareFeedID]
                    WHERE 1=1 ";

                // Dynamically build WHERE clause based on the User's UI selection
                if (travelDate.HasValue)
                {
                    query += " AND FF.[DepartureDate] >= @TravelDate AND FF.[DepartureDate] < DATEADD(day, 1, @TravelDate) ";
                }
                else if (entryStartDate.HasValue && entryEndDate.HasValue)
                {
                    query += " AND GR.[EntryDate] >= @EntryStartDate AND GR.[EntryDate] < DATEADD(day, 1, @EntryEndDate) ";
                }

                using (var command = new SqlCommand(query, connection))
                {
                    if (travelDate.HasValue)
                    {
                        command.Parameters.AddWithValue("@TravelDate", travelDate.Value.Date);
                    }
                    else if (entryStartDate.HasValue && entryEndDate.HasValue)
                    {
                        command.Parameters.AddWithValue("@EntryStartDate", entryStartDate.Value.Date);
                        command.Parameters.AddWithValue("@EntryEndDate", entryEndDate.Value.Date);
                    }

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                string dbSector = reader["Sector"]?.ToString() ?? "";
                                var parts = dbSector.Split('-');
                                string origin = parts.Length > 0 ? parts[0] : "";
                                string destination = parts.Length > 1 ? parts[1] : "";

                                DateTime departureDate = reader["DepartureDate"] != DBNull.Value ? Convert.ToDateTime(reader["DepartureDate"]) : DateTime.MinValue;
                                DateTime entryDate = reader["EntryDate"] != DBNull.Value ? Convert.ToDateTime(reader["EntryDate"]) : DateTime.MinValue;

                                string airline = reader["Airline"]?.ToString() ?? "";
                                string flightNo = reader["FlightNo"]?.ToString() ?? "";

                                results.Add(new AttemptRecord
                                {
                                    Id = reader["GroupRaisedEntryID"] != DBNull.Value ? Convert.ToInt32(reader["GroupRaisedEntryID"]) : 0,
                                    AqId = "AQ" + (reader["GroupRaisedEntryID"]?.ToString() ?? ""),
                                    ReqId = reader["GroupRaisedRefNo"]?.ToString() ?? "",
                                    Origin = origin,
                                    Destination = destination,
                                    TravelDate = departureDate != DateTime.MinValue ? departureDate.ToString("dd MMM yyyy") : "-",
                                    RawTravelDate = departureDate,
                                    EntryDate = entryDate != DateTime.MinValue ? entryDate.ToString("dd MMM yyyy") : "-",
                                    RawEntryDate = entryDate,
                                    AirlineCode = airline,
                                    FlightCode = airline + " " + flightNo,
                                    Pax = reader["RaisedPax"] != DBNull.Value ? Convert.ToInt32(reader["RaisedPax"]) : 0,
                                    AutoQuote = reader["GroupFare"] != DBNull.Value ? "₹" + reader["GroupFare"].ToString() : "0",
                                    OnlineFare = reader["Fare"] != DBNull.Value ? "₹" + Convert.ToDouble(reader["Fare"]).ToString("N0") : "0",
                                    Status = reader["Status"]?.ToString() ?? "",
                                    NegFare = reader["NegotiatedFare"] != DBNull.Value ? "₹" + reader["NegotiatedFare"].ToString() : "0",
                                    ConfStatus = reader["ConfirmationStatus"]?.ToString() ?? "",
                                    Remarks = reader["Remarks"]?.ToString() ?? "",
                                    PNR = reader["PNR"]?.ToString() ?? "-",
                                    OpenRemarks = reader["OpenRemarks"]?.ToString() ?? "-",
                                    Lead = reader["UserName"]?.ToString() ?? ""
                                });
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"DB Details Fetch Error: {ex.Message}"); }
                }
            }
            return results;
        }

        public async Task<List<(string Sector, DateTime TravelDate, int Count)>> GetBulkAttemptCountsDetailedAsync(List<string> sectors, DateTime startDate, DateTime endDate)
        {
            var results = new List<(string Sector, DateTime TravelDate, int Count)>();
            if (string.IsNullOrEmpty(_connectionString) || !sectors.Any()) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                var sectorParams = sectors.Select((s, i) => $"@sec{i}").ToList();
                string inClause = string.Join(",", sectorParams);

                string query = $@"
            SELECT FF.Sector, CAST(FF.[DepartureDate] AS DATE) as TravelDate, Count(GR.[GroupRaisedEntryID]) as AttemptCount
            FROM [Blackout].[GroupRaisedEntry] As GR
            LEFT JOIN Blackout.BlackoutFareFeed As FF ON GR.BlackoutFareFeedID = FF.BlackoutFareFeedID
            WHERE FF.[DepartureDate] >= @StartDate 
              AND FF.[DepartureDate] < DATEADD(day, 1, @EndDate)
              AND FF.Sector IN ({inClause})
            GROUP BY FF.Sector, CAST(FF.[DepartureDate] AS DATE)";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@StartDate", startDate.Date);
                    command.Parameters.AddWithValue("@EndDate", endDate.Date);
                    for (int i = 0; i < sectors.Count; i++) command.Parameters.AddWithValue(sectorParams[i], sectors[i]);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (reader["Sector"] != DBNull.Value && reader["TravelDate"] != DBNull.Value)
                                {
                                    results.Add((
                                        reader["Sector"].ToString() ?? "",
                                        Convert.ToDateTime(reader["TravelDate"]).Date,
                                        Convert.ToInt32(reader["AttemptCount"])
                                    ));
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"DB Detailed Bulk Fetch Error: {ex.Message}"); }
                }
            }
            return results;
        }

        public async Task<List<FareTrendRecord>> GetFareTrendAsync(string sector, DateTime departureDate, string airline, string flightNo)
        {
            var results = new List<FareTrendRecord>();
            if (string.IsNullOrEmpty(_connectionString)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                // Added LEFT JOIN to count attempts natively grouped by the Feed ID
                string query = @"
            SELECT 
                FF.Fare, 
                FF.EntryDate,
                COUNT(GR.GroupRaisedEntryID) as AttemptCount
            FROM [Blackout].[BlackoutFareFeed] FF
            LEFT JOIN [Blackout].[GroupRaisedEntry] GR ON FF.BlackoutFareFeedID = GR.BlackoutFareFeedID
            WHERE FF.Sector = @Sector 
              AND FF.DepartureDate >= @DepartureDate 
              AND FF.DepartureDate < DATEADD(day, 1, @DepartureDate)
              AND FF.Airline = @Airline 
              AND FF.FlightNo = @FlightNo 
            GROUP BY FF.Fare, FF.EntryDate
            ORDER BY FF.EntryDate ASC";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Sector", sector);
                    command.Parameters.AddWithValue("@DepartureDate", departureDate.Date);
                    command.Parameters.AddWithValue("@Airline", airline);
                    command.Parameters.AddWithValue("@FlightNo", flightNo);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new FareTrendRecord
                                {
                                    Fare = reader["Fare"] != DBNull.Value ? Convert.ToDouble(reader["Fare"]) : 0,
                                    EntryDate = reader["EntryDate"] != DBNull.Value ? Convert.ToDateTime(reader["EntryDate"]) : DateTime.MinValue,
                                    AttemptCount = reader["AttemptCount"] != DBNull.Value ? Convert.ToInt32(reader["AttemptCount"]) : 0
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DB Trend Fetch Error: {ex.Message}");
                    }
                }
            }
            return results;
        }

        public async Task<Dictionary<DateTime, (double LowestFare, DateTime EntryDate, string Airline, string FlightNo)>> GetLowestLatestFaresAsync(string sector, DateTime startDate, DateTime endDate)
        {
            var results = new Dictionary<DateTime, (double LowestFare, DateTime EntryDate, string Airline, string FlightNo)>();
            if (string.IsNullOrEmpty(_connectionString)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                // We use a second CTE (RankedFares) to rank the fares by price, 
                // which allows us to select the lowest fare without losing the Airline column.
                string query = @"
            WITH LatestFares AS (
                SELECT CAST(DepartureDate AS DATE) AS TravelDate, Airline, FlightNo, Fare, EntryDate,
                       ROW_NUMBER() OVER(PARTITION BY CAST(DepartureDate AS DATE), Airline, FlightNo ORDER BY EntryDate DESC) as rn
                FROM [Blackout].[BlackoutFareFeed]
                WHERE Sector = @Sector 
                  AND DepartureDate >= @StartDate 
                  AND DepartureDate < DATEADD(day, 1, @EndDate) 
                  AND EntryDate >= CAST(DATEADD(day, -3, GETDATE()) AS DATE)
            ),
            RankedFares AS (
                SELECT TravelDate, Fare, EntryDate, Airline, FlightNo,
                       ROW_NUMBER() OVER(PARTITION BY TravelDate ORDER BY Fare ASC, EntryDate DESC) as rankByFare
                FROM LatestFares
                WHERE rn = 1
            )
            SELECT TravelDate, Fare AS LowestFare, EntryDate, Airline, FlightNo
            FROM RankedFares
            WHERE rankByFare = 1";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Sector", sector);
                    command.Parameters.AddWithValue("@StartDate", startDate.Date);
                    command.Parameters.AddWithValue("@EndDate", endDate.Date);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (reader["TravelDate"] != DBNull.Value && reader["LowestFare"] != DBNull.Value)
                                {
                                    var travelDate = Convert.ToDateTime(reader["TravelDate"]).Date;
                                    var lowestFare = Convert.ToDouble(reader["LowestFare"]);
                                    var entryDate = Convert.ToDateTime(reader["EntryDate"]);
                                    var airline = reader["Airline"]?.ToString() ?? "Unknown";
                                    var flightNumber = reader["FlightNo"]?.ToString() ?? "Unknown";

                                    // Save the Tuple to the dictionary, now including the Airline
                                    results[travelDate] = (lowestFare, entryDate, airline, flightNumber);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"DB Lowest Fares Fetch Error: {ex.Message}"); }
                }
            }
            return results;
        }

        // 2. FARE TREND DB HOOK: Grabs the absolute latest fare AND its Entry Date
        public async Task<List<FlightFareRecord>> GetFlightLatestFaresAsync(string sector, DateTime travelDate)
        {
            var results = new List<FlightFareRecord>();
            if (string.IsNullOrEmpty(_connectionString)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                // CRITICAL FIX: The database will now strictly refuse to load any flight 
                // that hasn't had a fare update in the last 3 days!
                string query = @"
                    WITH LatestFares AS (
                        SELECT Sector, CAST(DepartureDate AS DATE) as TravelDate, Airline, FlightNo, Fare, EntryDate,
                               ROW_NUMBER() OVER(PARTITION BY Airline, FlightNo ORDER BY EntryDate DESC) as rn
                        FROM [Blackout].[BlackoutFareFeed]
                        WHERE Sector = @Sector 
                          AND DepartureDate >= @Date 
                          AND DepartureDate < DATEADD(day, 1, @Date)
                          AND EntryDate >= CAST(DATEADD(day, -3, GETDATE()) AS DATE)
                    )
                    SELECT Sector, TravelDate, Airline, FlightNo, Fare, EntryDate
                    FROM LatestFares 
                    WHERE rn = 1
                    ORDER BY Airline, FlightNo";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Sector", sector);
                    command.Parameters.AddWithValue("@Date", travelDate.Date);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new FlightFareRecord
                                {
                                    Sector = reader["Sector"]?.ToString() ?? sector,
                                    TravelDate = reader["TravelDate"] != DBNull.Value ? Convert.ToDateTime(reader["TravelDate"]) : travelDate,
                                    AirlineCode = reader["Airline"]?.ToString() ?? "",
                                    FlightNo = reader["FlightNo"]?.ToString() ?? "",
                                    LatestFare = reader["Fare"] != DBNull.Value ? Convert.ToDouble(reader["Fare"]) : 0,
                                    EntryDate = reader["EntryDate"] != DBNull.Value ? Convert.ToDateTime(reader["EntryDate"]) : DateTime.MinValue
                                });
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"DB Flight Fares Fetch Error: {ex.Message}"); }
                }
            }
            return results;
        }

        public async Task<int?> GetLatestBlackoutFareFeedIdAsync(string sector, DateTime departureDate, string airline, string flightNo)
        {
            if (string.IsNullOrEmpty(_connectionString)) return null;

            using (var connection = new SqlConnection(_connectionString))
            {
                // Finds the most recently updated FareFeedID for this specific flight
                string query = @"
            SELECT TOP 1 [BlackoutFareFeedID] 
            FROM [Blackout].[BlackoutFareFeed]
            WHERE Sector = @Sector 
              AND DepartureDate >= @Date 
              AND DepartureDate < DATEADD(day, 1, @Date)
              AND Airline = @Airline 
              AND FlightNo = @FlightNo
            ORDER BY EntryDate DESC";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Sector", sector);
                    command.Parameters.AddWithValue("@Date", departureDate.Date);
                    command.Parameters.AddWithValue("@Airline", airline);
                    command.Parameters.AddWithValue("@FlightNo", flightNo);

                    try
                    {
                        await connection.OpenAsync();
                        var result = await command.ExecuteScalarAsync();
                        if (result != null && result != DBNull.Value) return Convert.ToInt32(result);
                    }
                    catch (Exception ex) { Console.WriteLine($"FeedID Lookup Error: {ex.Message}"); }
                }
            }
            return null;
        }

        //public async Task<bool> BulkInsertGroupRaisedEntriesAsync(List<GroupRaisedEntryModel> entries)
        //{
        //    if (string.IsNullOrEmpty(_connectionString) || !entries.Any()) return false;

        //    using (var connection = new SqlConnection(_connectionString))
        //    {
        //        await connection.OpenAsync();
        //        using (var transaction = connection.BeginTransaction())
        //        {
        //            try
        //            {
        //                // Max params in SQL Server is 2100. We use ~15 per record.
        //                // 2100 / 16 = 131 max records per batch. We use 100 to be perfectly safe.
        //                int batchSize = 100;
        //                for (int i = 0; i < entries.Count; i += batchSize)
        //                {
        //                    var batch = entries.Skip(i).Take(batchSize).ToList();

        //                    using (var command = new SqlCommand())
        //                    {
        //                        command.Connection = connection;
        //                        command.Transaction = transaction;
        //                        var sqlBuilder = new System.Text.StringBuilder();

        //                        for (int j = 0; j < batch.Count; j++)
        //                        {
        //                            var entry = batch[j];
        //                            string p = $"@{j}_"; // Unique prefix for this specific row's parameters

        //                            sqlBuilder.AppendLine($@"
        //                            IF {p}GroupRaisedEntryID IS NOT NULL AND {p}GroupRaisedEntryID > 0
        //                            BEGIN
        //                                UPDATE [Blackout].[GroupRaisedEntry]
        //                                SET [PNR] = {p}PNR, [Status] = {p}Status, [Remarks] = {p}Remarks,
        //                                    [ConfirmationStatus] = {p}ConfirmationStatus, [OpenRemarks] = {p}OpenRemarks,
        //                                    [NegotiatedFare] = {p}NegotiatedFare, [ModifiedDate] = {p}ModifiedDate
        //                                WHERE [GroupRaisedEntryID] = {p}GroupRaisedEntryID
        //                            END
        //                            ELSE
        //                            BEGIN
        //                                INSERT INTO [Blackout].[GroupRaisedEntry] 
        //                                ([BlackoutFareFeedID], [GroupRaisedRefNo], [PNR], [RaisedPax], [GroupFare], 
        //                                 [UserName], [Status], [FDestID], [RaisedDate], [EntryDate], [Remarks], [ConfirmationStatus], [OpenRemarks], 
        //                                 [NegotiatedFare], [ModifiedDate])
        //                                VALUES 
        //                                ({p}BlackoutFareFeedID, {p}GroupRaisedRefNo, {p}PNR, {p}RaisedPax, {p}GroupFare, 
        //                                 {p}UserName, {p}Status, {p}FDestID, {p}RaisedDate, {p}EntryDate, {p}Remarks, {p}ConfirmationStatus, {p}OpenRemarks, 
        //                                 {p}NegotiatedFare, {p}ModifiedDate)
        //                            END;");

        //                            command.Parameters.AddWithValue($"{p}GroupRaisedEntryID", (object)entry.GroupRaisedEntryID ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}BlackoutFareFeedID", (object)entry.BlackoutFareFeedID ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}GroupRaisedRefNo", (object)entry.GroupRaisedRefNo ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}PNR", (object)entry.PNR ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}RaisedPax", entry.RaisedPax);
        //                            command.Parameters.AddWithValue($"{p}GroupFare", entry.GroupFare);
        //                            command.Parameters.AddWithValue($"{p}UserName", (object)entry.UserName ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}Status", entry.Status);
        //                            command.Parameters.AddWithValue($"{p}FDestID", (object)entry.FDestID ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}RaisedDate", entry.RaisedDate);
        //                            command.Parameters.AddWithValue($"{p}EntryDate", entry.EntryDate);
        //                            command.Parameters.AddWithValue($"{p}Remarks", (object)entry.Remarks ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}ConfirmationStatus", (object)entry.ConfirmationStatus ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}OpenRemarks", (object)entry.OpenRemarks ?? DBNull.Value);
        //                            command.Parameters.AddWithValue($"{p}NegotiatedFare", entry.NegotiatedFare);
        //                            command.Parameters.AddWithValue($"{p}ModifiedDate", entry.ModifiedDate);
        //                        }

        //                        command.CommandText = sqlBuilder.ToString();
        //                        await command.ExecuteNonQueryAsync();
        //                    }
        //                }
        //                await transaction.CommitAsync();
        //                return true;
        //            }
        //            catch (Exception ex)
        //            {
        //                await transaction.RollbackAsync();
        //                Console.WriteLine($"Bulk Insert Error: {ex.Message}");
        //                return false;
        //            }
        //        }
        //    }
        //}

        public async Task<bool> BulkInsertGroupRaisedEntriesAsync(List<GroupRaisedEntryModel> entries)
        {
            if (string.IsNullOrEmpty(_connectionString) || !entries.Any()) return false;

            // Using SqlBulkCopy with a Temp Table and MERGE drastically improves DTU usage over batched IF/ELSE parameter inserts
            DataTable dt = new DataTable();
            dt.Columns.Add("GroupRaisedEntryID", typeof(int));
            dt.Columns.Add("BlackoutFareFeedID", typeof(int));
            dt.Columns.Add("GroupRaisedRefNo", typeof(string));
            dt.Columns.Add("PNR", typeof(string));
            dt.Columns.Add("RaisedPax", typeof(int));
            dt.Columns.Add("GroupFare", typeof(decimal));
            dt.Columns.Add("UserName", typeof(string));
            dt.Columns.Add("Status", typeof(string));
            dt.Columns.Add("FDestID", typeof(string));
            dt.Columns.Add("RaisedDate", typeof(DateTime));
            dt.Columns.Add("EntryDate", typeof(DateTime));
            dt.Columns.Add("Remarks", typeof(string));
            dt.Columns.Add("ConfirmationStatus", typeof(string));
            dt.Columns.Add("OpenRemarks", typeof(string));
            dt.Columns.Add("NegotiatedFare", typeof(decimal));
            dt.Columns.Add("ModifiedDate", typeof(DateTime));

            foreach (var entry in entries)
            {
                dt.Rows.Add(
                    entry.GroupRaisedEntryID ?? (object)DBNull.Value,
                    entry.BlackoutFareFeedID ?? (object)DBNull.Value,
                    entry.GroupRaisedRefNo ?? (object)DBNull.Value,
                    entry.PNR ?? (object)DBNull.Value,
                    entry.RaisedPax,
                    entry.GroupFare,
                    entry.UserName ?? (object)DBNull.Value,
                    entry.Status,
                    entry.FDestID ?? (object)DBNull.Value,
                    entry.RaisedDate,
                    entry.EntryDate,
                    entry.Remarks ?? (object)DBNull.Value,
                    entry.ConfirmationStatus ?? (object)DBNull.Value,
                    entry.OpenRemarks ?? (object)DBNull.Value,
                    entry.NegotiatedFare,
                    entry.ModifiedDate
                );
            }

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // 1. Create a lightweight temporary table
                        string createTempTableSql = @"
                            CREATE TABLE #TempGroupRaisedEntry (
                                GroupRaisedEntryID int NULL,
                                BlackoutFareFeedID int NULL,
                                GroupRaisedRefNo nvarchar(255) NULL,
                                PNR nvarchar(50) NULL,
                                RaisedPax int,
                                GroupFare decimal(18, 2),
                                UserName nvarchar(255) NULL,
                                Status nvarchar(50),
                                FDestID nvarchar(50) NULL,
                                RaisedDate datetime,
                                EntryDate datetime,
                                Remarks nvarchar(max) NULL,
                                ConfirmationStatus nvarchar(50) NULL,
                                OpenRemarks nvarchar(max) NULL,
                                NegotiatedFare decimal(18, 2),
                                ModifiedDate datetime
                            )";

                        using (var cmd = new SqlCommand(createTempTableSql, connection, transaction))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // 2. Bulk copy the payload into the temp table
                        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                        {
                            bulkCopy.DestinationTableName = "#TempGroupRaisedEntry";
                            foreach (DataColumn col in dt.Columns)
                            {
                                bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                            }
                            await bulkCopy.WriteToServerAsync(dt);
                        }

                        // 3. Issue a highly optimized MERGE statement to perform the Upsert
                        string mergeSql = @"
                            MERGE [Blackout].[GroupRaisedEntry] AS target
                            USING #TempGroupRaisedEntry AS source
                            ON (target.[GroupRaisedEntryID] = source.[GroupRaisedEntryID] AND source.[GroupRaisedEntryID] IS NOT NULL)
                            WHEN MATCHED THEN 
                                UPDATE SET 
                                    [PNR] = source.[PNR],
                                    [Status] = source.[Status],
                                    [Remarks] = source.[Remarks],
                                    [ConfirmationStatus] = source.[ConfirmationStatus],
                                    [OpenRemarks] = source.[OpenRemarks],
                                    [NegotiatedFare] = source.[NegotiatedFare],
                                    [ModifiedDate] = source.[ModifiedDate]
                            WHEN NOT MATCHED BY TARGET THEN
                                INSERT ([BlackoutFareFeedID], [GroupRaisedRefNo], [PNR], [RaisedPax], [GroupFare], 
                                        [UserName], [Status], [FDestID], [RaisedDate], [EntryDate], [Remarks], [ConfirmationStatus], [OpenRemarks], 
                                        [NegotiatedFare], [ModifiedDate])
                                VALUES (source.[BlackoutFareFeedID], source.[GroupRaisedRefNo], source.[PNR], source.[RaisedPax], source.[GroupFare],
                                        source.[UserName], source.[Status], source.[FDestID], source.[RaisedDate], source.[EntryDate], source.[Remarks], source.[ConfirmationStatus], source.[OpenRemarks],
                                        source.[NegotiatedFare], source.[ModifiedDate]);
                            
                            DROP TABLE #TempGroupRaisedEntry;";

                        using (var cmd = new SqlCommand(mergeSql, connection, transaction))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }

                        await transaction.CommitAsync();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine($"Bulk Insert Error: {ex.Message}");
                        return false;
                    }
                }
            }
        }

        public async Task<int> GetOrInsertBlackoutFareFeedIdAsync(string sector, DateTime departureDate, string airline, string flightNo, decimal amount, string arrivalTime, string departureTime)
        {
            if (string.IsNullOrEmpty(_connectionString)) return 0;

            // GUARD: Absolute protection against SqlDateTime overflow crashes
            DateTime sqlSafeDate = departureDate.Year < 1753 ? DateTime.Today : departureDate.Date;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                // 1. Try to find the existing Feed ID
                string checkQuery = @"
                SELECT TOP 1 [BlackoutFareFeedID] 
                FROM [Blackout].[BlackoutFareFeed]
                WHERE Sector = @Sector 
                  AND DepartureDate >= @Date 
                  AND DepartureDate < DATEADD(day, 1, @Date)
                  AND Airline = @Airline 
                  AND FlightNo = @FlightNo
                ORDER BY EntryDate DESC";

                using (var checkCmd = new SqlCommand(checkQuery, connection))
                {
                    checkCmd.Parameters.AddWithValue("@Sector", sector ?? "");
                    checkCmd.Parameters.AddWithValue("@Date", sqlSafeDate);
                    checkCmd.Parameters.AddWithValue("@Airline", airline ?? "");
                    checkCmd.Parameters.AddWithValue("@FlightNo", flightNo ?? "");

                    try
                    {
                        var result = await checkCmd.ExecuteScalarAsync();
                        if (result != null && result != DBNull.Value)
                        {
                            int existingId = Convert.ToInt32(result);

                            // =========================================================
                            // CRITICAL FIX: Update the old '0' records with LIVE data!
                            // =========================================================
                            if (amount > 0)
                            {
                                string updateQuery = "UPDATE [Blackout].[BlackoutFareFeed] SET Fare = @Fare, DepartureTime = @DepTime, ArrivalTime = @ArrTime WHERE BlackoutFareFeedID = @Id";
                                using (var updateCmd = new SqlCommand(updateQuery, connection))
                                {
                                    updateCmd.Parameters.AddWithValue("@Fare", amount);
                                    updateCmd.Parameters.AddWithValue("@DepTime", string.IsNullOrEmpty(departureTime) ? "00:00:00" : departureTime);
                                    updateCmd.Parameters.AddWithValue("@ArrTime", string.IsNullOrEmpty(arrivalTime) ? "00:00:00" : arrivalTime);
                                    updateCmd.Parameters.AddWithValue("@Id", existingId);
                                    await updateCmd.ExecuteNonQueryAsync();
                                }
                            }
                            return existingId;
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Check Feed Error: {ex.Message}"); }
                }

                // 2. If it doesn't exist, Insert using the live API data!
                string insertQuery = @"
                INSERT INTO [Blackout].[BlackoutFareFeed] 
                    ([Sector], [Airline], [FlightNo], [DepartureDate], [DepartureTime], [ArrivalTime], [Fare], [EntryDate], [Remarks])
                VALUES 
                    (@Sector, @Airline, @FlightNo, @Date, @DepartureTime, @ArrivalTime, @Fare, GETDATE(), 'Auto-generated fallback from Upload');
                
                SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using (var insertCmd = new SqlCommand(insertQuery, connection))
                {
                    insertCmd.Parameters.AddWithValue("@Sector", sector ?? "");
                    insertCmd.Parameters.AddWithValue("@Airline", airline ?? "");
                    insertCmd.Parameters.AddWithValue("@FlightNo", flightNo ?? "");
                    insertCmd.Parameters.AddWithValue("@Date", sqlSafeDate);

                    insertCmd.Parameters.AddWithValue("@DepartureTime", string.IsNullOrEmpty(departureTime) ? "00:00:00" : departureTime);
                    insertCmd.Parameters.AddWithValue("@ArrivalTime", string.IsNullOrEmpty(arrivalTime) ? "00:00:00" : arrivalTime);
                    insertCmd.Parameters.AddWithValue("@Fare", amount);

                    try
                    {
                        var newIdResult = await insertCmd.ExecuteScalarAsync();
                        if (newIdResult != null && newIdResult != DBNull.Value)
                        {
                            return Convert.ToInt32(newIdResult);
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Fallback Feed Insert Error: {ex.Message}"); }
                }
            }
            return 0;
        }

        public async Task<List<string>> GetUserSectorAlertPreferencesAsync(string userName)
        {
            var sectors = new List<string>();
            if (string.IsNullOrEmpty(_connectionString)) return sectors;

            using (var connection = new SqlConnection(_connectionString))
            {
                string query = "SELECT [Sector] FROM [Blackout].[UserSectorAlertPreferences] WHERE [UserName] = @UserName";
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@UserName", userName);
                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                sectors.Add(reader["Sector"].ToString());
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Error fetching preferences: {ex.Message}"); }
                }
            }
            return sectors;
        }

        public async Task<bool> SaveUserSectorAlertPreferencesAsync(string userName, List<string> sectors)
        {
            if (string.IsNullOrEmpty(_connectionString)) return false;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                // Using a transaction to wipe the old preferences and insert the new ones safely
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        string deleteQuery = "DELETE FROM [Blackout].[UserSectorAlertPreferences] WHERE [UserName] = @UserName";
                        using (var deleteCmd = new SqlCommand(deleteQuery, connection, transaction))
                        {
                            deleteCmd.Parameters.AddWithValue("@UserName", userName);
                            await deleteCmd.ExecuteNonQueryAsync();
                        }

                        if (sectors != null && sectors.Any())
                        {
                            string insertQuery = "INSERT INTO [Blackout].[UserSectorAlertPreferences] ([UserName], [Sector]) VALUES (@UserName, @Sector)";
                            foreach (var sector in sectors)
                            {
                                using (var insertCmd = new SqlCommand(insertQuery, connection, transaction))
                                {
                                    insertCmd.Parameters.AddWithValue("@UserName", userName);
                                    insertCmd.Parameters.AddWithValue("@Sector", sector);
                                    await insertCmd.ExecuteNonQueryAsync();
                                }
                            }
                        }

                        await transaction.CommitAsync();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine($"Error saving preferences: {ex.Message}");
                        return false;
                    }
                }
            }
        }

        public async Task<Dictionary<string, int>> GetBulkAttemptCountsBySectorAsync(List<string> sectors, DateTime startDate, DateTime endDate)
        {
            var results = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(_connectionString) || !sectors.Any()) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                // Dynamically build parameterized IN clause
                var sectorParams = sectors.Select((s, i) => $"@sec{i}").ToList();
                string inClause = string.Join(",", sectorParams);

                string query = $@"
            SELECT FF.Sector, Count(GR.[GroupRaisedEntryID]) as AttemptCount
            FROM [Blackout].[GroupRaisedEntry] As GR
            LEFT JOIN Blackout.BlackoutFareFeed As FF ON GR.BlackoutFareFeedID = FF.BlackoutFareFeedID
            WHERE FF.[DepartureDate] >= @StartDate 
              AND FF.[DepartureDate] < DATEADD(day, 1, @EndDate)
              AND FF.Sector IN ({inClause})
            GROUP BY FF.Sector";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@StartDate", startDate.Date);
                    command.Parameters.AddWithValue("@EndDate", endDate.Date);
                    for (int i = 0; i < sectors.Count; i++) command.Parameters.AddWithValue(sectorParams[i], sectors[i]);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                string sec = reader["Sector"]?.ToString() ?? "";
                                int count = reader["AttemptCount"] != DBNull.Value ? Convert.ToInt32(reader["AttemptCount"]) : 0;
                                if (!string.IsNullOrEmpty(sec)) results[sec] = count;
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"DB Bulk Fetch Error: {ex.Message}"); }
                }
            }
            return results;
        }

        public async Task<Dictionary<DateTime, int>> GetBulkAttemptCountsByDateAsync(string sector, DateTime startDate, DateTime endDate)
        {
            var results = new Dictionary<DateTime, int>();
            if (string.IsNullOrEmpty(_connectionString)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                string query = @"
            SELECT CAST(FF.[DepartureDate] AS DATE) as TravelDate, Count(GR.[GroupRaisedEntryID]) as AttemptCount
            FROM [Blackout].[GroupRaisedEntry] As GR
            LEFT JOIN Blackout.BlackoutFareFeed As FF ON GR.BlackoutFareFeedID = FF.BlackoutFareFeedID
            WHERE FF.Sector = @Sector 
              AND FF.[DepartureDate] >= @StartDate 
              AND FF.[DepartureDate] < DATEADD(day, 1, @EndDate)
            GROUP BY CAST(FF.[DepartureDate] AS DATE)";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Sector", sector);
                    command.Parameters.AddWithValue("@StartDate", startDate.Date);
                    command.Parameters.AddWithValue("@EndDate", endDate.Date);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (reader["TravelDate"] != DBNull.Value)
                                {
                                    DateTime dt = Convert.ToDateTime(reader["TravelDate"]);
                                    int count = reader["AttemptCount"] != DBNull.Value ? Convert.ToInt32(reader["AttemptCount"]) : 0;
                                    results[dt.Date] = count;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"DB Bulk Date Fetch Error: {ex.Message}"); }
                }
            }
            return results;
        }

        //public async Task<List<FlightFareRecord>> GetBulkFlightLatestFaresAsync(string sector, DateTime startDate, DateTime endDate)
        //{
        //    var results = new List<FlightFareRecord>();
        //    if (string.IsNullOrEmpty(_connectionString)) return results;

        //    using (var connection = new SqlConnection(_connectionString))
        //    {
        //        string query = @"
        //    WITH LatestFares AS (
        //        SELECT Sector, CAST(DepartureDate AS DATE) as TravelDate, Airline, FlightNo, Fare, EntryDate,
        //               ROW_NUMBER() OVER(PARTITION BY CAST(DepartureDate AS DATE), Airline, FlightNo ORDER BY EntryDate DESC) as rn
        //        FROM [Blackout].[BlackoutFareFeed]
        //        WHERE Sector = @Sector 
        //          AND DepartureDate >= @StartDate 
        //          AND DepartureDate < DATEADD(day, 1, @EndDate)
        //          AND EntryDate >= CAST(DATEADD(day, -3, GETDATE()) AS DATE)
        //    )
        //    SELECT Sector, TravelDate, Airline, FlightNo, Fare, EntryDate
        //    FROM LatestFares 
        //    WHERE rn = 1";

        //        using (var command = new SqlCommand(query, connection))
        //        {
        //            command.Parameters.AddWithValue("@Sector", sector);
        //            command.Parameters.AddWithValue("@StartDate", startDate.Date);
        //            command.Parameters.AddWithValue("@EndDate", endDate.Date);

        //            try
        //            {
        //                await connection.OpenAsync();
        //                using (var reader = await command.ExecuteReaderAsync())
        //                {
        //                    while (await reader.ReadAsync())
        //                    {
        //                        results.Add(new FlightFareRecord
        //                        {
        //                            Sector = reader["Sector"]?.ToString() ?? sector,
        //                            TravelDate = reader["TravelDate"] != DBNull.Value ? Convert.ToDateTime(reader["TravelDate"]) : DateTime.MinValue,
        //                            AirlineCode = reader["Airline"]?.ToString() ?? "",
        //                            FlightNo = reader["FlightNo"]?.ToString() ?? "",
        //                            LatestFare = reader["Fare"] != DBNull.Value ? Convert.ToDouble(reader["Fare"]) : 0,
        //                            EntryDate = reader["EntryDate"] != DBNull.Value ? Convert.ToDateTime(reader["EntryDate"]) : DateTime.MinValue
        //                        });
        //                    }
        //                }
        //            }
        //            catch (Exception ex) { Console.WriteLine($"DB Bulk Fares Fetch Error: {ex.Message}"); }
        //        }
        //    }
        //    return results;
        //}
        public async Task<List<FlightFareRecord>> GetBulkFlightLatestFaresAsync(string sectorCsv, DateTime startDate, DateTime endDate)
        {
            var results = new List<FlightFareRecord>();
            if (string.IsNullOrEmpty(_connectionString) || string.IsNullOrWhiteSpace(sectorCsv)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                // PERFORMANCE UPGRADE: 
                // We use STRING_SPLIT to turn the massive comma-separated string back into 
                // individual rows that SQL Server can instantly join against.
                string query = @"
        WITH ValidSectors AS (
            SELECT value AS SectorName FROM STRING_SPLIT(@SectorsList, ',')
        ),
        LatestFares AS (
            SELECT FF.Sector, CAST(FF.DepartureDate AS DATE) as TravelDate, FF.Airline, FF.FlightNo, FF.Fare, FF.EntryDate,
                   ROW_NUMBER() OVER(PARTITION BY FF.Sector, CAST(FF.DepartureDate AS DATE), FF.Airline, FF.FlightNo ORDER BY FF.EntryDate DESC) as rn
            FROM [Blackout].[BlackoutFareFeed] FF
            INNER JOIN ValidSectors VS ON FF.Sector = VS.SectorName
            WHERE FF.DepartureDate >= @StartDate 
              AND FF.DepartureDate < DATEADD(day, 1, @EndDate)
              AND FF.EntryDate >= CAST(DATEADD(day, -3, GETDATE()) AS DATE)
        )
        SELECT Sector, TravelDate, Airline, FlightNo, Fare, EntryDate
        FROM LatestFares 
        WHERE rn = 1";

                using (var command = new SqlCommand(query, connection))
                {
                    // Increase the timeout to 60 seconds just in case the query is massive
                    command.CommandTimeout = 60;

                    command.Parameters.AddWithValue("@SectorsList", sectorCsv);
                    command.Parameters.AddWithValue("@StartDate", startDate.Date);
                    command.Parameters.AddWithValue("@EndDate", endDate.Date);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new FlightFareRecord
                                {
                                    Sector = reader["Sector"]?.ToString() ?? "",
                                    TravelDate = reader["TravelDate"] != DBNull.Value ? Convert.ToDateTime(reader["TravelDate"]) : DateTime.MinValue,
                                    AirlineCode = reader["Airline"]?.ToString() ?? "",
                                    FlightNo = reader["FlightNo"]?.ToString() ?? "",
                                    LatestFare = reader["Fare"] != DBNull.Value ? Convert.ToDouble(reader["Fare"]) : 0,
                                    EntryDate = reader["EntryDate"] != DBNull.Value ? Convert.ToDateTime(reader["EntryDate"]) : DateTime.MinValue
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DB Bulk Fares Fetch Error: {ex.Message}");
                    }
                }
            }
            return results;
        }
        public async Task<List<FareAuditDbRecord>> GetRecentFareAuditLogsAsync()
        {
            var logs = new List<FareAuditDbRecord>();
            //string connectionString = _config.GetConnectionString("DefaultConnection");

            using (var connection = new SqlConnection(_connectionString))
            {
                // Fetching the last 30 days of audit logs
                string query = @"
            SELECT * 
            FROM [dbo].[FareAudit] 
            WHERE requestDateTime >= DATEADD(day, -30, GETDATE()) AND logType = 'booking'
            ORDER BY requestDateTime DESC";

                using (var command = new SqlCommand(query, connection))
                {
                    await connection.OpenAsync();
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var record = new FareAuditDbRecord
                            {
                                id = reader["id"] != DBNull.Value ? Convert.ToInt32(reader["id"]) : 0,
                                bookingId = reader["bookingId"] != DBNull.Value ? reader["bookingId"].ToString() : "",
                                ticketId = reader["ticketId"] != DBNull.Value ? reader["ticketId"].ToString() : "",
                                fareLogId = reader["fareLogId"] != DBNull.Value ? reader["fareLogId"].ToString() : "",
                                logType = reader["logType"] != DBNull.Value ? reader["logType"].ToString() : "",
                                source = reader["source"] != DBNull.Value ? reader["source"].ToString() : "",
                                requestDateTime = reader["requestDateTime"] != DBNull.Value ? Convert.ToDateTime(reader["requestDateTime"]) : null,
                                pnr = reader["pnr"] != DBNull.Value ? reader["pnr"].ToString() : "",
                                flightSector = reader["flightSector"] != DBNull.Value ? reader["flightSector"].ToString() : "",
                                flightNumber = reader["flightNumber"] != DBNull.Value ? reader["flightNumber"].ToString() : "",
                                travelDateTime = reader["travelDateTime"] != DBNull.Value ? Convert.ToDateTime(reader["travelDateTime"]) : null,

                                fsSameFlightFare = reader["fsSameFlightFare"] != DBNull.Value ? Convert.ToDecimal(reader["fsSameFlightFare"]) : null,
                                fsLowestFlightFare = reader["fsLowestFlightFare"] != DBNull.Value ? Convert.ToDecimal(reader["fsLowestFlightFare"]) : null,
                                fsAvailableStock = reader["fsAvailableStock"] != DBNull.Value ? Convert.ToInt32(reader["fsAvailableStock"]) : null,
                                fsErrorMessage = reader["fsErrorMessage"] != DBNull.Value ? reader["fsErrorMessage"].ToString() : "",
                                fsAverageFare = reader["fsAverageFare"] != DBNull.Value ? Convert.ToDecimal(reader["fsAverageFare"]) : null,

                                aoSameFlightFare = reader["aoSameFlightFare"] != DBNull.Value ? Convert.ToDecimal(reader["aoSameFlightFare"]) : null,
                                aoLowestFare = reader["aoLowestFare"] != DBNull.Value ? Convert.ToDecimal(reader["aoLowestFare"]) : null,
                                aoAverageFare = reader["aoAverageFare"] != DBNull.Value ? Convert.ToDecimal(reader["aoAverageFare"]) : null,
                                aoErrorMessage = reader["aoErrorMessage"] != DBNull.Value ? reader["aoErrorMessage"].ToString() : "",

                                flightWiseTotalSeatsPurchased = reader["flightWiseTotalSeatsPurchased"] != DBNull.Value ? Convert.ToInt32(reader["flightWiseTotalSeatsPurchased"]) : null,
                                flightWiseTotalSeatsSold = reader["flightWiseTotalSeatsSold"] != DBNull.Value ? Convert.ToInt32(reader["flightWiseTotalSeatsSold"]) : null,
                                flightWiseTotalSeatsLeft = reader["flightWiseTotalSeatsLeft"] != DBNull.Value ? Convert.ToInt32(reader["flightWiseTotalSeatsLeft"]) : null,
                                flightWiseAverageCost = reader["flightWiseAverageCost"] != DBNull.Value ? Convert.ToDecimal(reader["flightWiseAverageCost"]) : null,

                                routeWiseSeatsPurchased = reader["routeWiseSeatsPurchased"] != DBNull.Value ? Convert.ToInt32(reader["routeWiseSeatsPurchased"]) : null,
                                routeWiseSeatsSold = reader["routeWiseSeatsSold"] != DBNull.Value ? Convert.ToInt32(reader["routeWiseSeatsSold"]) : null,
                                routeWiseSeatsLeft = reader["routeWiseSeatsLeft"] != DBNull.Value ? Convert.ToInt32(reader["routeWiseSeatsLeft"]) : null,
                                routeWiseAverageCost = reader["routeWiseAverageCost"] != DBNull.Value ? Convert.ToDecimal(reader["routeWiseAverageCost"]) : null,
                                averageSellFare = reader["averageSellFare"] != DBNull.Value ? Convert.ToDecimal(reader["averageSellFare"]) : null,

                                status = reader["status"] != DBNull.Value ? reader["status"].ToString() : "",
                                remark = reader["remark"] != DBNull.Value ? reader["remark"].ToString() : "",
                                taskCompletedDateTime = reader["taskCompletedDateTime"] != DBNull.Value ? Convert.ToDateTime(reader["taskCompletedDateTime"]) : null,

                                fsSameFlightStock = reader["fsSameFlightStock"] != DBNull.Value ? Convert.ToInt32(reader["fsSameFlightStock"]) : null,
                                fsLowestFareFlightNumber = reader["fsLowestFareFlightNumber"] != DBNull.Value ? reader["fsLowestFareFlightNumber"].ToString() : "",
                                fsLowestFareFlightDepartureTime = reader["fsLowestFareFlightDepartureTime"] != DBNull.Value ? reader["fsLowestFareFlightDepartureTime"].ToString() : "",

                                aoLowestFareFlightNumber = reader["aoLowestFareFlightNumber"] != DBNull.Value ? reader["aoLowestFareFlightNumber"].ToString() : "",
                                aoSameFlightStock = reader["aoSameFlightStock"] != DBNull.Value ? Convert.ToInt32(reader["aoSameFlightStock"]) : null,
                                aoLowestFareFlightDepartureTime = reader["aoLowestFareFlightDepartureTime"] != DBNull.Value ? reader["aoLowestFareFlightDepartureTime"].ToString() : "",

                                fareAuditRemarks = reader["fareAuditRemarks"] != DBNull.Value ? reader["fareAuditRemarks"].ToString() : "",
                                aoAvailableStock = reader["aoAvailableStock"] != DBNull.Value ? Convert.ToInt32(reader["aoAvailableStock"]) : null,
                                actualManagedFare = reader["actualManagedFare"] != DBNull.Value ? Convert.ToDecimal(reader["actualManagedFare"]) : null,
                                bookedPaxQty = reader["bookedPaxQty"] != DBNull.Value ? Convert.ToInt32(reader["bookedPaxQty"]) : null,

                                fsWindowedLowestFare = reader["fsWindowedLowestFare"] != DBNull.Value ? Convert.ToDecimal(reader["fsWindowedLowestFare"]) : null,
                                fsWindowedLowestFareFlightNumber = reader["fsWindowedLowestFareFlightNumber"] != DBNull.Value ? reader["fsWindowedLowestFareFlightNumber"].ToString() : "",
                                fsWindowedLowestFareDepartureTime = reader["fsWindowedLowestFareDepartureTime"] != DBNull.Value ? reader["fsWindowedLowestFareDepartureTime"].ToString() : "",

                                aoWindowedLowestFare = reader["aoWindowedLowestFare"] != DBNull.Value ? Convert.ToDecimal(reader["aoWindowedLowestFare"]) : null,
                                aoWindowedLowestFareFlightNumber = reader["aoWindowedLowestFareFlightNumber"] != DBNull.Value ? reader["aoWindowedLowestFareFlightNumber"].ToString() : "",
                                aoWindowedLowestFareDepartureTime = reader["aoWindowedLowestFareDepartureTime"] != DBNull.Value ? reader["aoWindowedLowestFareDepartureTime"].ToString() : ""
                            };
                            logs.Add(record);
                        }
                    }
                }
            }
            return logs;
        }

        public async Task<bool> CreateUserSessionAsync(UserSession session)
        {
            var query = @"
        INSERT INTO insight.UserSessions 
        (SessionId, Username, UserRole, IpAddress, UserAgent, BrowserName, OperatingSystem, LoginTime, IsActive)
        VALUES 
        (@SessionId, @Username, @UserRole, @IpAddress, @UserAgent, @BrowserName, @OperatingSystem, @LoginTime, 1)";

            // Replace with your standard DB execution logic (e.g., using Dapper or standard SqlCommand)
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SessionId", session.SessionId);
                    command.Parameters.AddWithValue("@Username", session.Username);
                    command.Parameters.AddWithValue("@UserRole", session.UserRole);
                    command.Parameters.AddWithValue("@IpAddress", (object)session.IpAddress ?? DBNull.Value);
                    command.Parameters.AddWithValue("@UserAgent", (object)session.UserAgent ?? DBNull.Value);
                    command.Parameters.AddWithValue("@BrowserName", (object)session.BrowserName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@OperatingSystem", (object)session.OperatingSystem ?? DBNull.Value);
                    command.Parameters.AddWithValue("@LoginTime", session.LoginTime);

                    int result = await command.ExecuteNonQueryAsync();
                    return result > 0;
                }
            }
        }

        public async Task<bool> CloseUserSessionAsync(Guid sessionId, DateTime logoutTime)
        {
            var query = @"
        UPDATE insight.UserSessions 
        SET LogoutTime = @LogoutTime,
            DurationSeconds = DATEDIFF(SECOND, LoginTime, @LogoutTime),
            IsActive = 0
        WHERE SessionId = @SessionId AND IsActive = 1";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SessionId", sessionId);
                    command.Parameters.AddWithValue("@LogoutTime", logoutTime);

                    int result = await command.ExecuteNonQueryAsync();
                    return result > 0;
                }
            }
        }
        public async Task<bool> InsertUserActivityLogAsync(UserActivityLog log)
        {
            var query = @"
        INSERT INTO insight.UserActivityLogs 
        (SessionId, Username, UserRole, HttpMethod, ActivityType, ControllerName, ActionName, RequestPath, QueryString, StatusCode, ExecutionDurationMs, Timestamp)
        VALUES 
        (@SessionId, @Username, @UserRole, @HttpMethod, @ActivityType, @ControllerName, @ActionName, @RequestPath, @QueryString, @StatusCode, @ExecutionDurationMs, @Timestamp)";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SessionId", (object)log.SessionId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@Username", log.Username);
                    command.Parameters.AddWithValue("@UserRole", log.UserRole);
                    command.Parameters.AddWithValue("@HttpMethod", log.HttpMethod);
                    command.Parameters.AddWithValue("@ActivityType", log.ActivityType);
                    command.Parameters.AddWithValue("@ControllerName", log.ControllerName);
                    command.Parameters.AddWithValue("@ActionName", log.ActionName);
                    command.Parameters.AddWithValue("@RequestPath", log.RequestPath);
                    command.Parameters.AddWithValue("@QueryString", (object)log.QueryString ?? DBNull.Value);
                    command.Parameters.AddWithValue("@StatusCode", log.StatusCode);
                    command.Parameters.AddWithValue("@ExecutionDurationMs", log.ExecutionDurationMs);
                    command.Parameters.AddWithValue("@Timestamp", log.Timestamp);

                    int result = await command.ExecuteNonQueryAsync();
                    return result > 0;
                }
            }
        }
        public async Task<bool> InsertUserUploadLogAsync(UserUploadLog log)
        {
            var query = @"
        INSERT INTO insight.UserUploadLogs 
        (SessionId, Username, FileName, FileExtension, FileSizeBytes, RecordsParsed, IsSuccessful, ErrorMessage, Timestamp)
        VALUES 
        (@SessionId, @Username, @FileName, @FileExtension, @FileSizeBytes, @RecordsParsed, @IsSuccessful, @ErrorMessage, @Timestamp)";

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SessionId", (object)log.SessionId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@Username", log.Username);
                    command.Parameters.AddWithValue("@FileName", log.FileName);
                    command.Parameters.AddWithValue("@FileExtension", log.FileExtension);
                    command.Parameters.AddWithValue("@FileSizeBytes", log.FileSizeBytes);
                    command.Parameters.AddWithValue("@RecordsParsed", log.RecordsParsed);
                    command.Parameters.AddWithValue("@IsSuccessful", log.IsSuccessful);
                    command.Parameters.AddWithValue("@ErrorMessage", (object)log.ErrorMessage ?? DBNull.Value);
                    command.Parameters.AddWithValue("@Timestamp", log.Timestamp);

                    int result = await command.ExecuteNonQueryAsync();
                    return result > 0;
                }
            }
        }
        public async Task<IEnumerable<UserSession>> GetUserSessionsAsync()
        {
            var query = "SELECT TOP 1000 * FROM insight.UserSessions ORDER BY LoginTime DESC";
            var results = new List<UserSession>();
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        results.Add(new UserSession
                        {
                            SessionId = reader.GetGuid(0),
                            Username = reader.GetString(1),
                            UserRole = reader.GetString(2),
                            IpAddress = reader.IsDBNull(3) ? null : reader.GetString(3),
                            UserAgent = reader.IsDBNull(4) ? null : reader.GetString(4),
                            BrowserName = reader.IsDBNull(5) ? null : reader.GetString(5),
                            OperatingSystem = reader.IsDBNull(6) ? null : reader.GetString(6),
                            LoginTime = reader.GetDateTime(7),
                            LogoutTime = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                            DurationSeconds = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                            IsActive = reader.GetBoolean(10)
                        });
                    }
                }
            }
            return results;
        }

        public async Task<IEnumerable<UserActivityLog>> GetUserActivityLogsAsync()
        {
            var query = "SELECT TOP 2000 * FROM insight.UserActivityLogs ORDER BY Timestamp DESC";
            var results = new List<UserActivityLog>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        results.Add(new UserActivityLog
                        {
                            ActivityId = reader.GetInt64(0),
                            SessionId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                            Username = reader.GetString(2),
                            UserRole = reader.GetString(3),
                            HttpMethod = reader.GetString(4),
                            ActivityType = reader.GetString(5),
                            Area = reader.IsDBNull(6) ? null : reader.GetString(6),
                            ControllerName = reader.GetString(7),
                            ActionName = reader.GetString(8),
                            RequestPath = reader.GetString(9),
                            QueryString = reader.IsDBNull(10) ? null : reader.GetString(10),
                            StatusCode = reader.GetInt32(11),
                            ExecutionDurationMs = reader.GetInt32(12),
                            Timestamp = reader.GetDateTime(13)
                        });
                    }
                }
            }
            return results;
        }

        public async Task<IEnumerable<UserUploadLog>> GetUserUploadLogsAsync()
        {
            var query = "SELECT TOP 1000 * FROM insight.UserUploadLogs ORDER BY Timestamp DESC";
            var results = new List<UserUploadLog>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        results.Add(new UserUploadLog
                        {
                            UploadId = reader.GetInt64(0),
                            ActivityId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                            SessionId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                            Username = reader.GetString(3),
                            FileName = reader.GetString(4),
                            FileExtension = reader.GetString(5),
                            FileSizeBytes = reader.GetInt64(6),
                            RecordsParsed = reader.GetInt32(7),
                            IsSuccessful = reader.GetBoolean(8),
                            ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
                            Timestamp = reader.GetDateTime(10)
                        });
                    }
                }
            }
            return results;
        }

        //public async Task<Dictionary<string, (DateTime TravelDate, double LatestFare, double Avg7DayFare, int FlightCount)>> GetBulkDemandFareStatsAsync(string sectorCsv, DateTime startDate, DateTime endDate)
        //{
        //    // CRITICAL FIX: Make the dictionary completely case-insensitive
        //    var results = new Dictionary<string, (DateTime, double, double, int)>(StringComparer.OrdinalIgnoreCase);
        //    if (string.IsNullOrEmpty(_connectionString) || string.IsNullOrWhiteSpace(sectorCsv)) return results;

        //    using (var connection = new SqlConnection(_connectionString))
        //    {
        //        // CRITICAL FIX: Added LTRIM and RTRIM everywhere to prevent invisible SQL spaces from breaking the JOINs
        //        string query = @"
        //WITH ValidSectors AS (
        //    SELECT LTRIM(RTRIM(value)) AS SectorName FROM STRING_SPLIT(@SectorsList, ',')
        //),
        //BaseData AS (
        //    SELECT 
        //        LTRIM(RTRIM(FF.Sector)) AS Sector, 
        //        CAST(FF.DepartureDate AS DATE) AS TravelDate, 
        //        FF.Fare, 
        //        FF.EntryDate,
        //        FF.Airline,
        //        FF.FlightNo
        //    FROM [Blackout].[BlackoutFareFeed] FF
        //    INNER JOIN ValidSectors VS ON LTRIM(RTRIM(FF.Sector)) = VS.SectorName
        //    WHERE FF.DepartureDate >= @StartDate 
        //      AND FF.DepartureDate < DATEADD(day, 1, @EndDate)
        //),
        //RankedFares AS (
        //    SELECT 
        //        Sector, 
        //        TravelDate, 
        //        Fare, 
        //        EntryDate,
        //        DENSE_RANK() OVER(PARTITION BY Sector, TravelDate ORDER BY CAST(EntryDate AS DATE) DESC) AS DayRank,
        //        ROW_NUMBER() OVER(PARTITION BY Sector, TravelDate ORDER BY EntryDate DESC) AS rn
        //    FROM BaseData
        //),
        //LatestFares AS (
        //    SELECT Sector, TravelDate, Fare as LatestFare
        //    FROM RankedFares
        //    WHERE rn = 1
        //),
        //AvgFares AS (
        //    SELECT Sector, TravelDate, AVG(Fare) as Avg7DayFare
        //    FROM RankedFares
        //    WHERE DayRank <= 7
        //    GROUP BY Sector, TravelDate
        //),
        //FlightCounts AS (
        //    SELECT Sector, TravelDate, COUNT(DISTINCT CONCAT(Airline, FlightNo)) as FlightCount
        //    FROM BaseData
        //    GROUP BY Sector, TravelDate
        //)
        //SELECT 
        //    L.Sector, 
        //    L.TravelDate, 
        //    L.LatestFare, 
        //    ISNULL(A.Avg7DayFare, 0) AS Avg7DayFare, 
        //    FC.FlightCount
        //FROM LatestFares L
        //JOIN AvgFares A ON L.Sector = A.Sector AND L.TravelDate = A.TravelDate
        //JOIN FlightCounts FC ON L.Sector = FC.Sector AND L.TravelDate = FC.TravelDate;";

        //        using (var command = new SqlCommand(query, connection))
        //        {
        //            command.CommandTimeout = 120;
        //            command.Parameters.AddWithValue("@SectorsList", sectorCsv);
        //            command.Parameters.AddWithValue("@StartDate", startDate.Date);
        //            command.Parameters.AddWithValue("@EndDate", endDate.Date);

        //            try
        //            {
        //                await connection.OpenAsync();
        //                using (var reader = await command.ExecuteReaderAsync())
        //                {
        //                    while (await reader.ReadAsync())
        //                    {
        //                        // CRITICAL FIX: Trim and force uppercase when saving to dictionary
        //                        string sector = (reader["Sector"].ToString() ?? "").Trim().ToUpper();
        //                        DateTime travelDate = Convert.ToDateTime(reader["TravelDate"]);
        //                        double latestFare = Convert.ToDouble(reader["LatestFare"]);
        //                        double avg7DayFare = Convert.ToDouble(reader["Avg7DayFare"]);
        //                        int flightCount = Convert.ToInt32(reader["FlightCount"]);

        //                        string compositeKey = $"{sector}|{travelDate:yyyy-MM-dd}";

        //                        results[compositeKey] = (travelDate, latestFare, avg7DayFare, flightCount);
        //                    }
        //                }
        //            }
        //            catch (Exception ex) { Console.WriteLine($"DB Bulk Demand Fares Error: {ex.Message}"); }
        //        }
        //    }
        //    return results;
        //}
        public async Task<Dictionary<string, (DateTime TravelDate, double LatestFare, double Avg7DayFare, int FlightCount)>> GetBulkDemandFareStatsAsync(string sectorCsv, DateTime startDate, DateTime endDate)
        {
            // CRITICAL FIX: Make the dictionary completely case-insensitive
            var results = new Dictionary<string, (DateTime, double, double, int)>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(_connectionString) || string.IsNullOrWhiteSpace(sectorCsv)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                string query = @"
        WITH ValidSectors AS (
            SELECT LTRIM(RTRIM(value)) AS SectorName FROM STRING_SPLIT(@SectorsList, ',')
        ),
        BaseData AS (
            SELECT 
                LTRIM(RTRIM(FF.Sector)) AS Sector, 
                CAST(FF.DepartureDate AS DATE) AS TravelDate, 
                FF.Fare, 
                FF.EntryDate,
                FF.Airline,
                FF.FlightNo
            FROM [Blackout].[BlackoutFareFeed] FF
            INNER JOIN ValidSectors VS ON LTRIM(RTRIM(FF.Sector)) = VS.SectorName
            WHERE FF.DepartureDate >= @StartDate 
              AND FF.DepartureDate < DATEADD(day, 1, @EndDate)
        ),
        RankedFares AS (
            SELECT 
                Sector, 
                TravelDate, 
                Fare, 
                EntryDate,
                DENSE_RANK() OVER(PARTITION BY Sector, TravelDate ORDER BY CAST(EntryDate AS DATE) DESC) AS DayRank,
                ROW_NUMBER() OVER(PARTITION BY Sector, TravelDate ORDER BY EntryDate DESC) AS rn
            FROM BaseData
        ),
        LatestFares AS (
            SELECT Sector, TravelDate, Fare as LatestFare
            FROM RankedFares
            WHERE rn = 1
        ),
        AvgFares AS (
            SELECT Sector, TravelDate, AVG(Fare) as Avg7DayFare
            FROM RankedFares
            WHERE DayRank <= 7
            GROUP BY Sector, TravelDate
        ),
        FlightCounts AS (
            SELECT 
                Sector, 
                TravelDate, 
                -- CRITICAL FIX: Only count flights where the FlightNo does NOT contain a slash (/)
                COUNT(DISTINCT CASE WHEN ISNULL(FlightNo, '') NOT LIKE '%/%' THEN CONCAT(Airline, FlightNo) END) as FlightCount
            FROM BaseData
            GROUP BY Sector, TravelDate
        )
        SELECT 
            L.Sector, 
            L.TravelDate, 
            L.LatestFare, 
            ISNULL(A.Avg7DayFare, 0) AS Avg7DayFare, 
            FC.FlightCount
        FROM LatestFares L
        JOIN AvgFares A ON L.Sector = A.Sector AND L.TravelDate = A.TravelDate
        JOIN FlightCounts FC ON L.Sector = FC.Sector AND L.TravelDate = FC.TravelDate;";

                using (var command = new SqlCommand(query, connection))
                {
                    command.CommandTimeout = 120;
                    command.Parameters.AddWithValue("@SectorsList", sectorCsv);
                    command.Parameters.AddWithValue("@StartDate", startDate.Date);
                    command.Parameters.AddWithValue("@EndDate", endDate.Date);

                    try
                    {
                        await connection.OpenAsync();
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                // Trim and force uppercase when saving to dictionary
                                string sector = (reader["Sector"].ToString() ?? "").Trim().ToUpper();
                                DateTime travelDate = Convert.ToDateTime(reader["TravelDate"]);
                                double latestFare = Convert.ToDouble(reader["LatestFare"]);
                                double avg7DayFare = Convert.ToDouble(reader["Avg7DayFare"]);
                                int flightCount = Convert.ToInt32(reader["FlightCount"]);

                                string compositeKey = $"{sector}|{travelDate:yyyy-MM-dd}";

                                results[compositeKey] = (travelDate, latestFare, avg7DayFare, flightCount);
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"DB Bulk Demand Fares Error: {ex.Message}"); }
                }
            }
            return results;
        }
    }
}