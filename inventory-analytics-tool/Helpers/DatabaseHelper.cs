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
        Task<Dictionary<DateTime, (double LowestFare, DateTime EntryDate)>> GetLowestLatestFaresAsync(string sector, DateTime startDate, DateTime endDate);
        Task<List<FlightFareRecord>> GetFlightLatestFaresAsync(string sector, DateTime travelDate);
        Task<int?> GetLatestBlackoutFareFeedIdAsync(string sector, DateTime departureDate, string airline, string flightNo);
        Task<bool> BulkInsertGroupRaisedEntriesAsync(List<GroupRaisedEntryModel> entries);
        Task<List<AttemptRecord>> GetAllAttemptDetailsAsync(DateTime? entryStartDate, DateTime? entryEndDate, DateTime? travelDate);
        Task<int> GetOrInsertBlackoutFareFeedIdAsync(string sector, DateTime departureDate, string airline, string flightNo);
        Task<List<string>> GetUserSectorAlertPreferencesAsync(string userName);
        Task<bool> SaveUserSectorAlertPreferencesAsync(string userName, List<string> sectors);
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
                      AND FF.[DepartureDate] <= @EndDate";

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
                    query += " AND CAST(FF.[DepartureDate] AS DATE) = @TravelDate ";
                }
                else if (entryStartDate.HasValue && entryEndDate.HasValue)
                {
                    query += " AND CAST(GR.[EntryDate] AS DATE) >= @EntryStartDate AND CAST(GR.[EntryDate] AS DATE) <= @EntryEndDate ";
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
              AND CAST(FF.DepartureDate AS DATE) = CAST(@DepartureDate AS DATE) 
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

        public async Task<Dictionary<DateTime, (double LowestFare, DateTime EntryDate)>> GetLowestLatestFaresAsync(string sector, DateTime startDate, DateTime endDate)
        {
            var results = new Dictionary<DateTime, (double, DateTime)>();
            if (string.IsNullOrEmpty(_connectionString)) return results;

            using (var connection = new SqlConnection(_connectionString))
            {
                string query = @"
            WITH LatestFares AS (
                SELECT CAST(DepartureDate AS DATE) AS TravelDate, Airline, FlightNo, Fare, EntryDate,
                       ROW_NUMBER() OVER(PARTITION BY CAST(DepartureDate AS DATE), Airline, FlightNo ORDER BY EntryDate DESC) as rn
                FROM [Blackout].[BlackoutFareFeed]
                WHERE Sector = @Sector
                  AND DepartureDate >= @StartDate 
                  AND DepartureDate < DATEADD(day, 1, @EndDate) 
                  AND EntryDate >= CAST(CAST(DATEADD(day, -3, GETDATE()) AS DATE) AS DATETIME)
            )
            SELECT TravelDate, MIN(Fare) AS LowestFare, MAX(EntryDate) AS EntryDate 
            FROM LatestFares
            WHERE rn = 1
            GROUP BY TravelDate";

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

                                    // Save the Tuple to the dictionary
                                    results[travelDate] = (lowestFare, entryDate);
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
                          AND CAST(DepartureDate AS DATE) = CAST(@Date AS DATE)
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
              AND CAST(DepartureDate AS DATE) = CAST(@Date AS DATE)
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

        public async Task<bool> BulkInsertGroupRaisedEntriesAsync(List<GroupRaisedEntryModel> entries)
        {
            if (string.IsNullOrEmpty(_connectionString) || !entries.Any()) return false;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        string query = @"
                    -- If the Excel row has an ID, UPDATE the existing record!
                    IF @GroupRaisedEntryID IS NOT NULL AND @GroupRaisedEntryID > 0
                    BEGIN
                        UPDATE [Blackout].[GroupRaisedEntry]
                        SET [PNR] = @PNR,
                            [Status] = @Status,
                            [Remarks] = @Remarks,
                            [ConfirmationStatus] = @ConfirmationStatus,
                            [OpenRemarks] = @OpenRemarks,
                            [NegotiatedFare] = @NegotiatedFare,
                            [ModifiedDate] = @ModifiedDate
                        WHERE [GroupRaisedEntryID] = @GroupRaisedEntryID
                    END
                    -- If the Excel row is brand new (blank ID), INSERT it!
                    ELSE
                    BEGIN
                        INSERT INTO [Blackout].[GroupRaisedEntry] 
                        ([BlackoutFareFeedID], [GroupRaisedRefNo], [PNR], [RaisedPax], [GroupFare], 
                         [UserName], [Status], [FDestID], [RaisedDate], [EntryDate], [Remarks], [ConfirmationStatus], [OpenRemarks], 
                         [NegotiatedFare], [ModifiedDate])
                        VALUES 
                        (@BlackoutFareFeedID, @GroupRaisedRefNo, @PNR, @RaisedPax, @GroupFare, 
                         @UserName, @Status, @FDestID, @RaisedDate, @EntryDate, @Remarks, @ConfirmationStatus, @OpenRemarks, 
                         @NegotiatedFare, @ModifiedDate)
                    END";

                        foreach (var entry in entries)
                        {
                            using (var command = new SqlCommand(query, connection, transaction))
                            {
                                command.Parameters.AddWithValue("@GroupRaisedEntryID", (object)entry.GroupRaisedEntryID ?? DBNull.Value);
                                command.Parameters.AddWithValue("@BlackoutFareFeedID", (object)entry.BlackoutFareFeedID ?? DBNull.Value);
                                command.Parameters.AddWithValue("@GroupRaisedRefNo", (object)entry.GroupRaisedRefNo ?? DBNull.Value);
                                command.Parameters.AddWithValue("@PNR", (object)entry.PNR ?? DBNull.Value);
                                command.Parameters.AddWithValue("@RaisedPax", entry.RaisedPax);
                                command.Parameters.AddWithValue("@GroupFare", entry.GroupFare);
                                command.Parameters.AddWithValue("@UserName", (object)entry.UserName ?? DBNull.Value);
                                command.Parameters.AddWithValue("@Status", entry.Status);
                                command.Parameters.AddWithValue("@FDestID", (object)entry.FDestID ?? DBNull.Value);
                                command.Parameters.AddWithValue("@RaisedDate", entry.RaisedDate);
                                command.Parameters.AddWithValue("@EntryDate", entry.EntryDate);
                                command.Parameters.AddWithValue("@Remarks", (object)entry.Remarks ?? DBNull.Value);
                                command.Parameters.AddWithValue("@ConfirmationStatus", (object)entry.ConfirmationStatus ?? DBNull.Value);
                                command.Parameters.AddWithValue("@OpenRemarks", (object)entry.OpenRemarks ?? DBNull.Value);
                                command.Parameters.AddWithValue("@NegotiatedFare", entry.NegotiatedFare);
                                command.Parameters.AddWithValue("@ModifiedDate", entry.ModifiedDate);

                                await command.ExecuteNonQueryAsync();
                            }
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

        public async Task<int> GetOrInsertBlackoutFareFeedIdAsync(string sector, DateTime departureDate, string airline, string flightNo)
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
              AND CAST(DepartureDate AS DATE) = CAST(@Date AS DATE)
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
                        if (result != null && result != DBNull.Value) return Convert.ToInt32(result);
                    }
                    catch (Exception ex) { Console.WriteLine($"Check Feed Error: {ex.Message}"); }
                }

                // 2. If it doesn't exist, Insert!
                // FIX: Using SCOPE_IDENTITY() instead of OUTPUT INSERTED so table triggers don't crash it.
                string insertQuery = @"
    INSERT INTO [Blackout].[BlackoutFareFeed] 
    ([Sector], [Airline], [FlightNo], [DepartureDate], [DepartureTime], [ArrivalTime], [Fare], [EntryDate], [Remarks])
    VALUES 
    (@Sector, @Airline, @FlightNo, @Date, '00:00:00', '00:00:00', 0, GETDATE(), 'Auto-generated fallback from Upload');
    
    SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using (var insertCmd = new SqlCommand(insertQuery, connection))
                {
                    insertCmd.Parameters.AddWithValue("@Sector", sector ?? "");
                    insertCmd.Parameters.AddWithValue("@Airline", airline ?? "");
                    insertCmd.Parameters.AddWithValue("@FlightNo", flightNo ?? "");
                    insertCmd.Parameters.AddWithValue("@Date", sqlSafeDate);

                    try
                    {
                        var newIdResult = await insertCmd.ExecuteScalarAsync();
                        if (newIdResult != null && newIdResult != DBNull.Value)
                        {
                            return Convert.ToInt32(newIdResult);
                        }
                    }
                    catch (Exception ex)
                    {
                        // If it fails now, it will output the exact reason to your console
                        Console.WriteLine($"Fallback Feed Insert Error: {ex.Message}");
                    }
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
    }
}