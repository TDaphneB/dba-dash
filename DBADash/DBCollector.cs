﻿using Microsoft.SqlServer.Management.Common;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Polly;
using Serilog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.Caching;
using System.Text;
using System.Xml;
namespace DBADash
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CollectionType
    {
        General,
        Performance,
        Infrequent,
        AgentJobs,
        Databases,
        DatabasesHADR,
        SysConfig,
        Drives,
        DBConfig,
        DBFiles,
        Corruption,
        OSInfo,
        TraceFlags,
        DriversWMI,
        CPU,
        BlockingSnapshot,
        IOStats,
        Waits,
        Backups,
        LogRestores,
        ServerProperties,
        ServerExtraProperties,
        OSLoadedModules,
        DBTuningOptions,
        AzureDBResourceStats,
        AzureDBServiceObjectives,
        AzureDBElasticPoolResourceStats,
        SlowQueries,
        LastGoodCheckDB,
        Alerts,
        ObjectExecutionStats,
        ServerPrincipals,
        ServerRoleMembers,
        ServerPermissions,
        DatabasePrincipals,
        DatabaseRoleMembers,
        DatabasePermissions,
        CustomChecks,
        PerformanceCounters,
        VLF,
        DatabaseMirroring,
        Jobs,
        JobHistory,
        AvailabilityReplicas,
        AvailabilityGroups,
        ResourceGovernorConfiguration,
        DatabaseQueryStoreOptions,
        AzureDBResourceGovernance,
        RunningQueries
    }

    public enum HostPlatform
    {
        Linux,
        Windows
    }


    public class DBCollector
    {
        public DataSet Data;
        string _connectionString;
        private DataTable dtErrors;
        public bool LogInternalPerformanceCounters=false;
        private DataTable dtInternalPerfCounters;
        private bool noWMI;
        public Int32 PerformanceCollectionPeriodMins = 60;
        string computerName;
        Int64 editionId;
        readonly CollectionType[] azureCollectionTypes = new CollectionType[] { CollectionType.SlowQueries, CollectionType.AzureDBElasticPoolResourceStats, CollectionType.AzureDBServiceObjectives, CollectionType.AzureDBResourceStats, CollectionType.CPU, CollectionType.DBFiles, CollectionType.General, CollectionType.Performance, CollectionType.Databases, CollectionType.DBConfig, CollectionType.TraceFlags, CollectionType.ObjectExecutionStats, CollectionType.BlockingSnapshot, CollectionType.IOStats, CollectionType.Waits, CollectionType.ServerProperties, CollectionType.DBTuningOptions, CollectionType.SysConfig, CollectionType.DatabasePrincipals, CollectionType.DatabaseRoleMembers, CollectionType.DatabasePermissions, CollectionType.Infrequent, CollectionType.OSInfo,CollectionType.CustomChecks,CollectionType.PerformanceCounters,CollectionType.VLF, CollectionType.DatabaseQueryStoreOptions, CollectionType.AzureDBResourceGovernance, CollectionType.RunningQueries};
        readonly CollectionType[] azureOnlyCollectionTypes = new CollectionType[] { CollectionType.AzureDBElasticPoolResourceStats, CollectionType.AzureDBResourceStats, CollectionType.AzureDBServiceObjectives, CollectionType.AzureDBResourceGovernance };
        readonly CollectionType[] azureMasterOnlyCollectionTypes = new CollectionType[] { CollectionType.AzureDBElasticPoolResourceStats };

        public Int64 SlowQueryThresholdMs = -1;
        public Int32 SlowQueryMaxMemoryKB { get; set; } = 4096;
        public bool UseDualEventSession { get; set; } = true;
        public PlanCollectionThreshold PlanThreshold = PlanCollectionThreshold.PlanCollectionDisabledThreshold;


        private bool IsAzure = false;
        private bool isAzureMasterDB = false;
        private string instanceName;
        string dbName;
        string productVersion;
        public Int32 RetryCount=1;
        public Int32 RetryInterval = 30;
        private HostPlatform platform;
        public DateTime JobLastModified=DateTime.MinValue;
        private bool IsHadrEnabled=false;
        private Policy retryPolicy;
        private DatabaseEngineEdition engineEdition;
        CacheItemPolicy policy = new CacheItemPolicy
        {
            SlidingExpiration = TimeSpan.FromMinutes(60)
        };
        MemoryCache cache = MemoryCache.Default;

        public int Job_instance_id {
            get
            {
                if (Data.Tables.Contains("JobHistory"))
                {
                    DataTable jh = Data.Tables["JobHistory"];
                    if (jh.Rows.Count > 0)
                    {
                        job_instance_id = Convert.ToInt32(jh.Compute("max(instance_id)", string.Empty));
                    }                   
                }
                return job_instance_id;
            }
            set {
                job_instance_id=value;
            }
        }
        int job_instance_id = 0;

        public DateTime GetJobLastModified()
        {
            using (SqlConnection cn = new SqlConnection(_connectionString))
            using (SqlCommand cmd = new SqlCommand("SELECT MAX(date_modified) FROM msdb.dbo.sysjobs", cn))
            {
                cn.Open();
                var result = cmd.ExecuteScalar();
                if (result == DBNull.Value)
                {
                    return DateTime.MinValue;
                }
                else
                {
                    return (DateTime)result;
                }
            }
        }


        public bool IsXESupported()
        {
            return DBADashConnection.IsXESupported(productVersion);
        }

        public bool IsQueryStoreSupported()
        {
            if (IsAzure)
            {
                return true;
            }
            else
            {
                if (productVersion.StartsWith("8.") || productVersion.StartsWith("9.") || productVersion.StartsWith("10.") || productVersion.StartsWith("11.") || productVersion.StartsWith("12."))
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        public DBCollector(string connectionString, bool noWMI)
        {
            this.noWMI = noWMI;
            startup(connectionString, null);
        }

        private void logError(Exception ex,string errorSource, string errorContext = "Collect")
        {
            Log.Error(ex,"{ErrorContext} {ErrorSource}" ,errorContext,errorSource);
            logDBError(errorSource, ex.ToString(), errorContext);
        }

        private void logDBError(string errorSource, string errorMessage, string errorContext = "Collect")
        {
            var rError = dtErrors.NewRow();
            rError["ErrorSource"] = errorSource;
            rError["ErrorMessage"] = errorMessage;
            rError["ErrorContext"] = errorContext;
            dtErrors.Rows.Add(rError);
        }

        private void logInternalPerformanceCounter(string objectName, string counterName,string instanceName, decimal counterValue)
        {
            if (LogInternalPerformanceCounters)
            {
                if (dtInternalPerfCounters == null)
                {
                    dtInternalPerfCounters = new DataTable("InternalPerformanceCounters");
                    dtInternalPerfCounters.Columns.Add("SnapshotDate");
                    dtInternalPerfCounters.Columns.Add("object_name");
                    dtInternalPerfCounters.Columns.Add("counter_name");
                    dtInternalPerfCounters.Columns.Add("instance_name");
                    dtInternalPerfCounters.Columns.Add("cntr_value", typeof(decimal));
                    dtInternalPerfCounters.Columns.Add("cntr_type", typeof(int));
                    Data.Tables.Add(dtInternalPerfCounters);
                }
                var row = dtInternalPerfCounters.NewRow();
                row["SnapshotDate"] = DateTime.UtcNow;
                row["object_name"] = objectName;
                row["counter_name"] = counterName;
                row["instance_name"] = instanceName;
                row["cntr_value"] = counterValue;
                row["cntr_type"] = 65792;
                dtInternalPerfCounters.Rows.Add(row);
            }
        }

        private void startup(string connectionString, string connectionID)
        {    
            retryPolicy = Policy.Handle<Exception>()
                .WaitAndRetry(new[]
                {
                                TimeSpan.FromSeconds(2),
                                TimeSpan.FromSeconds(5),
                                TimeSpan.FromSeconds(10)
                }, (exception, timeSpan, retryCount, context) =>
                {
                    logError(exception,(string)context.OperationKey, "Collect[Retrying]");
                });
            _connectionString = connectionString;
            Data = new DataSet("DBADash");
            dtErrors = new DataTable("Errors");
            dtErrors.Columns.Add("ErrorSource");
            dtErrors.Columns.Add("ErrorMessage");
            dtErrors.Columns.Add("ErrorContext");

            Data.Tables.Add(dtErrors);

            retryPolicy.Execute(
                context => GetInstance(connectionID),
                new Context("Instance")
              );
            
        }

        public DBCollector(string connectionString, string connectionID)
        {           
            startup(connectionString, connectionID);
        }

        public void RemoveEventSessions()
        {
            if (IsXESupported())
            {
                string removeSQL;
                if (IsAzure)
                {
                    removeSQL = Properties.Resources.SQLRemoveEventSessionsAzure;
                }
                else
                {
                    removeSQL = Properties.Resources.SQLRemoveEventSessions;
                }  
                using (var cn = new SqlConnection(_connectionString))
                {
                    using (var cmd = new SqlCommand(removeSQL, cn))
                    {
                        cn.Open();
                        cmd.ExecuteScalar();
                    }
                }
            }
        }

        public void StopEventSessions()
        {
            if (IsXESupported())
            {
                string removeSQL;
                if (IsAzure)
                {
                    removeSQL = Properties.Resources.SQLStopEventSessionsAzure;
                }
                else
                {
                    removeSQL = Properties.Resources.SQLStopEventSessions;
                }
                using (var cn = new SqlConnection(_connectionString))
                {
                    using (var cmd = new SqlCommand(removeSQL, cn))
                    {
                        cn.Open();
                        cmd.ExecuteScalar();
                    }
                }
            }
        }

        public void GetInstance(string connectionID)
        {
            var dt = getDT("DBADash", Properties.Resources.SQLInstance);
            dt.Columns.Add("AgentVersion", typeof(string));
            dt.Columns.Add("ConnectionID", typeof(string));
            dt.Columns.Add("AgentHostName", typeof(string));
            dt.Rows[0]["AgentVersion"] = Assembly.GetEntryAssembly().GetName().Version;
            dt.Rows[0]["AgentHostName"] = Environment.MachineName;

            editionId = (Int64)dt.Rows[0]["EditionId"];
            computerName = (string)dt.Rows[0]["ComputerNamePhysicalNetBIOS"];
            dbName = (string)dt.Rows[0]["DBName"];
            instanceName = (string)dt.Rows[0]["Instance"];
            productVersion = (string)dt.Rows[0]["ProductVersion"];
            string hostPlatform = (string)dt.Rows[0]["host_platform"];
            engineEdition = (DatabaseEngineEdition)Convert.ToInt32(dt.Rows[0]["EngineEdition"]);

            if (!Enum.TryParse(hostPlatform, out platform))
            {
                Log.Error("GetInstance: host_platform parse error");
                logDBError("Instance", "host_platform parse error");
                platform = HostPlatform.Windows;
            }
            if(platform == HostPlatform.Linux)
            {
                noWMI = true;
            }
            if (editionId == 1674378470)
            {
                IsAzure = true;
                if (dbName == "master")
                {
                    isAzureMasterDB = true;
                }
            }

            if (computerName.Length == 0)
            {
                noWMI = true;
            }
            if (connectionID == null)
            {
                if (IsAzure)
                {
                    dt.Rows[0]["ConnectionID"] = instanceName + "|" + dbName;
                    noWMI = true;
                    // dt.Rows[0]["Instance"] = instanceName + "|" + dbName;
                }
                else
                {
                    dt.Rows[0]["ConnectionID"] = instanceName;
                }
            }
            else
            {
                dt.Rows[0]["ConnectionID"] = connectionID;
            }
            IsHadrEnabled = dt.Rows[0]["IsHadrEnabled"] == DBNull.Value ? false : Convert.ToBoolean(dt.Rows[0]["IsHadrEnabled"]);

            Data.Tables.Add(dt);


        }

        public void Collect(CollectionType[] collectionTypes)
        {
            foreach (CollectionType type in collectionTypes)
            {
                Collect(type);
            }
        }
        private string enumToString(Enum en)
        {
            return Enum.GetName(en.GetType(), en);
        }

        private bool collectionTypeIsApplicable(CollectionType collectionType)
        {
            var collectionTypeString = enumToString(collectionType);
            if (collectionType == CollectionType.DatabaseQueryStoreOptions && !IsQueryStoreSupported())
            {
                // Query store not supported on this instance
                return false;
            }
            else if (Data.Tables.Contains(collectionTypeString))
            {
                // Already collected
                return false;
            }
            else if (IsAzure && (!azureCollectionTypes.Contains(collectionType)))
            {
                // Collection Type doesn't apply to AzureDB
                return false;
            }
            else if (!IsAzure && azureOnlyCollectionTypes.Contains(collectionType))
            {
                // Collection Type doesn't apply to normal standalone instance
                return false;
            }
            else if (azureMasterOnlyCollectionTypes.Contains(collectionType) && !isAzureMasterDB)
            {
                // Collection type only applies to Azure master db
                return false;
            }
            else
            {
                return true;
            }
        }

        public void Collect(CollectionType collectionType)
        {
            var collectionTypeString = enumToString(collectionType);

            if (!collectionTypeIsApplicable(collectionType))
            {
                return;
            }
          
            // Group collection types
            if (collectionType == CollectionType.General)
            {
                Collect(CollectionType.ServerProperties);
                Collect(CollectionType.Databases);              
                Collect(CollectionType.SysConfig);
                Collect(CollectionType.Drives);
                Collect(CollectionType.DBFiles);
                Collect(CollectionType.Backups);
                Collect(CollectionType.LogRestores);
                Collect(CollectionType.ServerExtraProperties);
                Collect(CollectionType.DBConfig);
                Collect(CollectionType.Corruption);
                Collect(CollectionType.OSInfo);
                Collect(CollectionType.TraceFlags);                              
                Collect(CollectionType.DBTuningOptions);
                Collect(CollectionType.AzureDBServiceObjectives);
                Collect(CollectionType.LastGoodCheckDB);
                Collect(CollectionType.Alerts);
                Collect(CollectionType.CustomChecks);
                Collect(CollectionType.DatabaseMirroring);
                Collect(CollectionType.Jobs);
                Collect(CollectionType.AzureDBResourceGovernance);
                return;
            }
            else if (collectionType == CollectionType.Performance)
            {
                Collect(CollectionType.ObjectExecutionStats);
                Collect(CollectionType.CPU);
                //Collect(CollectionType.BlockingSnapshot);
                Collect(CollectionType.IOStats);
                Collect(CollectionType.Waits);
                Collect(CollectionType.AzureDBResourceStats);
                Collect(CollectionType.AzureDBElasticPoolResourceStats);
                Collect(CollectionType.SlowQueries);
                Collect(CollectionType.PerformanceCounters);
                Collect(CollectionType.JobHistory);
                Collect(CollectionType.RunningQueries);
                if (IsHadrEnabled)
                {
                    Collect(CollectionType.DatabasesHADR);
                    Collect(CollectionType.AvailabilityReplicas);
                    Collect(CollectionType.AvailabilityGroups);
                }
                return;
            }
            else if(collectionType == CollectionType.Infrequent)
            {
                Collect(CollectionType.ServerPrincipals);
                Collect(CollectionType.ServerRoleMembers);
                Collect(CollectionType.ServerPermissions);
                Collect(CollectionType.DatabasePrincipals);
                Collect(CollectionType.DatabaseRoleMembers);
                Collect(CollectionType.DatabasePermissions);
                Collect(CollectionType.VLF);
                Collect(CollectionType.DriversWMI);
                Collect(CollectionType.OSLoadedModules);
                Collect(CollectionType.ResourceGovernorConfiguration);
                Collect(CollectionType.DatabaseQueryStoreOptions);
                return;
                
            }

            try
            {
                retryPolicy.Execute(
                  context => collect(collectionType),
                  new Context(collectionTypeString)
                );
            }
            catch (Exception ex)
            {
                logError(ex, collectionTypeString);
            }
            if(collectionType == CollectionType.RunningQueries)
            {
                collectText();
                collectPlans();
            }
          
        }

        static string ByteArrayToHexString(byte[] bytes)
        {
            string hex = BitConverter.ToString(bytes);
            return hex.Replace("-", "");
        }

        private void collectPlans()
        {
            if (Data.Tables.Contains("RunningQueries") && PlanThreshold.PlanCollectionEnabled)
            {
                var plansSQL = getPlansSQL();
                if (!String.IsNullOrEmpty(plansSQL))
                {
                    using (var cn = new SqlConnection(_connectionString))
                    using (var da = new SqlDataAdapter(plansSQL, cn))
                    {
                        var dt = new DataTable("QueryPlans");                      
                        da.Fill(dt);
                        if (dt.Rows.Count > 0)
                        {
                            dt.Columns.Add("query_plan_hash", typeof(byte[]));
                            dt.Columns.Add("query_plan_compressed", typeof(byte[]));
                            foreach (DataRow r in dt.Rows){                         
                                try
                                {
                                    string strPlan = r["query_plan"] == DBNull.Value ? string.Empty : (string)r["query_plan"];
                                    r["query_plan_compressed"] = SchemaSnapshotDB.Zip(strPlan);
                                    var hash = GetPlanHash(strPlan);
                                    r["query_plan_hash"] = hash;
                                }
                                catch(Exception ex)
                                {
                                    Log.Error(ex, "Error processing query plans");
                                }
                            }
                            dt.Columns.Remove("query_plan");

                            Data.Tables.Add(dt);
                        }
                        logInternalPerformanceCounter("DBADash", "Count of plans collected", "", dt.Rows.Count); // Count of plans actually collected - might be less than the list of plans we wanted to collect
                    }
                }
                else
                {
                    logInternalPerformanceCounter("DBADash", "Count of plans collected", "", 0); // Count of plans actually collected - might be less than the list of plans we wanted to collect
                }
            }
        }


        ///<summary>
        ///Get the query plan hash from a  string of the plan XML
        ///</summary>
        public static byte[] GetPlanHash(string strPlan)
        {
            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(strPlan)))
            {
                ms.Position = 0;
                using (var xr = new XmlTextReader(ms))
                {
                    while (xr.Read())
                    {
                        if (xr.Name== "StmtSimple")
                        {
                            string strHash= xr.GetAttribute("QueryPlanHash");
                            return StringToByteArray(strHash);
                        }                        
                    }
                }
            }
            return new byte[0];
        }


        public static byte[] StringToByteArray(string hex)
        {
            if (hex.StartsWith("0x"))
            {
                hex = hex.Remove(0, 2);
            }
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }


        ///<summary>
        ///Generate a SQL query to get the query plan text for running queries. Captured plan handles get cached with a call to CacheCollectedPlans later <br/>
        ///Limits the cost associated with plan capture - less plans to capture, send and process<br/>
        ///Note: Caching takes query_plan_hash into account as a statement can get recompiled without the plan handle changing.
        ///</summary>
        public string getPlansSQL()
        {
            var plans = getPlansList();
            var sb = new StringBuilder();
            sb.Append(@"DECLARE @plans TABLE(plan_handle VARBINARY(64),statement_start_offset int,statement_end_offset int)
INSERT INTO @plans(plan_handle,statement_start_offset,statement_end_offset)
VALUES");

            // Already have a distinct list by plan handle, hash and offsets.  
            // Filter this list by plans not already colllected and get a distinct list by handle and offsets (excluding the hash as this can cause duplicates in rare cases)
            var collectList =  plans.Where(p => !cache.Contains(p.Key))
                .GroupBy(p => new { p.PlanHandle, p.StartOffset, p.EndOffset })
                .Select(p => p.First())
                .ToList();

            collectList.ForEach(p =>sb.AppendFormat("{3}(0x{0},{1},{2}),", ByteArrayToHexString(p.PlanHandle), p.StartOffset, p.EndOffset,Environment.NewLine));

            Log.Information("Plans {0}, {1} to collect from {2}", plans.Count, collectList.Count,instanceName);

            logInternalPerformanceCounter("DBADash", "Count of plans meeting threshold for collection", "", plans.Count); // Total number of plans that meet the threshold for collection
            logInternalPerformanceCounter("DBADash", "Count of plans to collect", "", collectList.Count); // Total number of plans we want to collect (plans that meet the threshold that are not cached)
            logInternalPerformanceCounter("DBADash", "Count of plans from cache", "", plans.Count- collectList.Count); // Plan count we didn't collect because they have been collected previously and we cached the handles/hashes.

            if (collectList.Count == 0)
            {
                return string.Empty;
            }
            else
            {
                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine();
                sb.Append(@"SELECT t.plan_handle,
        t.statement_start_offset,
        t.statement_end_offset,
        pln.dbid,
        pln.objectid,
        pln.encrypted,
        pln.query_plan
FROM @plans t 
CROSS APPLY sys.dm_exec_text_query_plan(t.plan_handle,t.statement_start_offset,t.statement_end_offset) pln");
                return sb.ToString();
            }
        }

        ///<summary>
        ///Get a list of plan handles from RunningQueries including statement start/end offsets as we want to capture plans at the statement level. Query plan hash is used to detect changes in the plan for caching purposes <br/>
        ///Capture a distinct list so we collect the plan for each statement once even if there are multiple instances of statements running with the same plan.<br/>
        ///Filter for plans matching the specified threshold to limit the plans captured to the ones that are likely to be of interest<br/>
        ///</summary>
        private List<Plan> getPlansList()
        {
            if (Data.Tables.Contains("RunningQueries"))
            {
                DataTable dt = Data.Tables["RunningQueries"];
                var plans = (from r in dt.AsEnumerable()
                             where r["plan_handle"] != DBNull.Value && r["query_plan_hash"] != DBNull.Value && r["statement_start_offset"] != DBNull.Value && r["statement_end_offset"] != DBNull.Value
                             group r by new Plan((byte[])r["plan_handle"], (byte[])r["query_plan_hash"], (int)r["statement_start_offset"], (int)r["statement_end_offset"]) into g
                             where g.Sum(r => Convert.ToInt32(r["cpu_time"])) >= PlanThreshold.CPUThreshold || g.Sum(r => Convert.ToInt32(r["granted_query_memory"])) >= PlanThreshold.MemoryGrantThreshold || g.Count() >= PlanThreshold.CountThreshold || g.Max(r=> ((DateTime)r["SnapshotDateUTC"]).Subtract((DateTime)r["last_request_start_time_utc"])).TotalMilliseconds >= PlanThreshold.DurationThreshold 
                             select g.Key).Distinct().ToList();
                return plans;
            }
            else
            {
                return new List<Plan>();
            }
        }

        ///<summary>
        ///Collect query text associated with captured running queries
        ///</summary>
        private void collectText()
        {
            if (Data.Tables.Contains("RunningQueries"))
            {
                var handlesSQL = getTextFromHandlesSQL();
                if (!String.IsNullOrEmpty(handlesSQL))
                {
                    using (var cn = new SqlConnection(_connectionString))
                    using (var da = new SqlDataAdapter(handlesSQL, cn))
                    {
                        var dt = new DataTable("QueryText");
                        da.Fill(dt);
                        if (dt.Rows.Count > 0)
                        {
                            Data.Tables.Add(dt);
                        }
                        logInternalPerformanceCounter("DBADash", "Count of text collected", "", dt.Rows.Count); // Count of text collected from sql_handles
                        logInternalPerformanceCounter("DBADash", "Count of running queries", "", Data.Tables["RunningQueries"].Rows.Count); // Total number of running queries
                    }                   
                }
            }
        }

        ///<summary>
        ///Once written to the destination, call this function to cache the plan handles and query plan hash. If the plan is cached it won't be collected in future.<br/>
        ///Note: We are just caching the plan handle and hash with the statement offsets.
        ///</summary>
        public void CacheCollectedPlans()
        {
            if (Data.Tables.Contains("QueryPlans"))
            {
                var dt = Data.Tables["QueryPlans"];
                foreach (DataRow r in dt.Rows)
                {
                    if (r["query_plan_hash"] != DBNull.Value)
                    {
                        var plan = new Plan((byte[])r["plan_handle"], (byte[])r["query_plan_hash"], (int)r["statement_start_offset"], (int)r["statement_end_offset"]);
                        cache.Add(plan.Key, "", policy);
                    }
                }
            }
        }

        ///<summary>
        ///Once written to the destination, call this function to cache the sql_handles for captured query text. If the handle is cached it won't be collected in future.<br/>
        ///Note: We capture text at the batch level and can use the statement offsets to get the statement text.
        ///</summary>
        public void CacheCollectedText()
        {
            if (Data.Tables.Contains("QueryText"))
            {
                var dt = Data.Tables["QueryText"];
                foreach(DataRow r in dt.Rows)
                {
                    cache.Add(ByteArrayToHexString((byte[])r["sql_handle"]), "",policy);
                }
            }
        }

        ///<summary>
        ///Generate a SQL query to get the query text associated with the plan handles for running queries
        ///</summary>
        private string getTextFromHandlesSQL()
        {
            var handles = runningQueriesHandles();
            Int32 cnt = 0;
            Int32 cacheCount = 0;
            var sb = new StringBuilder();
            sb.Append(@"DECLARE @handles TABLE(sql_handle VARBINARY(64))
INSERT INTO @handles(sql_handle)
VALUES
");
            foreach (string strHandle in handles)
            {
                if (!cache.Contains(strHandle))
                {
                    cnt += 1;
                    sb.Append(string.Format("(0x{0}),", strHandle));
                }
                else
                {
                    cacheCount += 1;
                }
            }

            logInternalPerformanceCounter("DBADash", "Distinct count of text (sql_handle)", "", handles.Count); // Total number of distinct sql_handles
            logInternalPerformanceCounter("DBADash", "Count of text (sql_handle) to collect", "", cnt); // Count of sql_handles we need to collect
            logInternalPerformanceCounter("DBADash", "Count of text (sql_handle) from cache", "", handles.Count - cnt); // Count of sql_handles we didn't need to collect becasue they were collected previously and we cached the sql_handle.

            if ((cnt + cacheCount) > 0)
            {
                Log.Information("QueryText: {0} from cache, {1} to collect from {2}", cacheCount, cnt, instanceName);
            }
            if (cnt == 0)
            {
                return string.Empty;
            }
            else
            {
                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine();
                sb.Append(@"SELECT H.sql_handle,
    txt.dbid,
    txt.objectid as object_id,
    txt.encrypted,
    txt.text
FROM @handles H 
CROSS APPLY sys.dm_exec_sql_text(H.sql_handle) txt");
                return sb.ToString();
            }
        }

        ///<summary>
        ///Get a distinct list of sql_handle for running queries.  The handles are later used to capture query text
        ///</summary>
        private List<string> runningQueriesHandles()
        {
            var handles = (from r in Data.Tables["RunningQueries"].AsEnumerable()
                           where r["sql_handle"] != DBNull.Value
                           select ByteArrayToHexString((byte[])r["sql_handle"])).Distinct().ToList();
            return handles;
        }

        private void collect(CollectionType collectionType)
        {
            var collectionTypeString = enumToString(collectionType);
            // Add params where required
            SqlParameter[] param = null;
            if (collectionType == CollectionType.JobHistory)
            {
                param = new SqlParameter[] { new SqlParameter { DbType = DbType.Int32, Value = Job_instance_id, ParameterName = "instance_id" }, new SqlParameter { DbType = DbType.Int32, ParameterName = "run_date", Value = Convert.ToInt32(DateTime.Now.AddDays(-7).ToString("yyyyMMdd")) } };
            }
            else if (collectionType == CollectionType.AzureDBResourceStats || collectionType == CollectionType.AzureDBElasticPoolResourceStats)
            {
                param = new SqlParameter[] { new SqlParameter("Date", DateTime.UtcNow.AddMinutes(-PerformanceCollectionPeriodMins)) };
            }
            else if (collectionType == CollectionType.CPU)
            {
                param = new SqlParameter[] { new SqlParameter("TOP", PerformanceCollectionPeriodMins) };
            }

            if (collectionType == CollectionType.Drives)
            {
                if (platform == HostPlatform.Windows) // drive collection not supported on linux
                {
                    collectDrives();
                }
            }
            else if (collectionType == CollectionType.ServerExtraProperties)
            {
                collectServerExtraProperties();
            }
            else if (collectionType == CollectionType.DriversWMI)
            {
                collectDriversWMI();
            }
            else if (collectionType == CollectionType.SlowQueries)
            {
                if (SlowQueryThresholdMs >= 0 && (!(IsAzure && isAzureMasterDB)))
                {
                     collectSlowQueries();
                }
            }
            else if (collectionType == CollectionType.PerformanceCounters)
            {
                collectPerformanceCounters();
            }
            else if (collectionType == CollectionType.Jobs)
            {
                var currentJobModified = GetJobLastModified();
                if (currentJobModified > JobLastModified)
                {
                    var ss = new SchemaSnapshotDB(_connectionString, new SchemaSnapshotDBOptions());
                    ss.SnapshotJobs(ref Data);
                    JobLastModified = currentJobModified;
                }
            }
            else if (collectionType == CollectionType.ResourceGovernorConfiguration)
            {
                if (engineEdition == DatabaseEngineEdition.Enterprise && !IsAzure)
                {
                    collectResourceGovernor();                  
                }
            }
            else
            {
                addDT(collectionTypeString, Properties.Resources.ResourceManager.GetString("SQL" + collectionTypeString, Properties.Resources.Culture), param);
            }
        }

        private void collectResourceGovernor()
        {
            var ss = new SchemaSnapshotDB(_connectionString);
            var dtRG = ss.ResourceGovernorConfiguration();
            Data.Tables.Add(dtRG);
        }

        private void collectPerformanceCounters()
        {
  
            string xml = PerformanceCounters.PerformanceCountersXML;
            if (xml.Length > 0)
            {
       
                string sql = Properties.Resources.ResourceManager.GetString("SQLPerformanceCounters", Properties.Resources.Culture);
                using (var cn = new SqlConnection(_connectionString))
                {
                    using (var da = new SqlDataAdapter(sql, cn))
                    {
                        cn.Open();
                        var ds = new DataSet();
                        SqlParameter pCountersXML = new SqlParameter("CountersXML", PerformanceCounters.PerformanceCountersXML)
                        {
                            SqlDbType = SqlDbType.Xml
                        };
                        da.SelectCommand.CommandTimeout = 60;
                        da.SelectCommand.Parameters.Add(pCountersXML);
                        da.Fill(ds);


                        var dt = ds.Tables[0];
                        if (ds.Tables.Count == 2)
                        {
                            var userDT = ds.Tables[1];
                            if (dt.Columns.Count == userDT.Columns.Count)
                            {
                                try
                                {
                                    for (Int32 i = 0; i < (dt.Columns.Count - 1); i++)
                                    {
                                        if (dt.Columns[i].ColumnName != userDT.Columns[i].ColumnName)
                                        {
                                            throw new Exception(String.Format("Invalid schema for custom metrics.  Expected column '{0}' in position {1} instead of '{2}'", dt.Columns[i].ColumnName, i + 1, userDT.Columns[i].ColumnName));
                                        }
                                        if (dt.Columns[i].DataType != userDT.Columns[i].DataType)
                                        {
                                            throw new Exception(String.Format("Invalid schema for custom metrics.  Column {0} expected data type is {1} instead of {2}", dt.Columns[i].ColumnName, dt.Columns[i].DataType.Name, userDT.Columns[i].DataType.Name));
                                        }
                                    }
                                    dt.Merge(userDT);
                                }
                                catch (Exception ex)
                                {
                                    logError(ex,"PerformanceCounters");
                                }
                            }
                            else
                            {
                                throw new Exception($"Invalid schema for custom metrics. Expected {dt.Columns.Count} columns instead of {userDT.Columns.Count}.");
                            }
                        }
                        ds.Tables.Remove(dt);
                        dt.TableName = "PerformanceCounters";
                        Data.Tables.Add(dt);
                    }
                }
            }
            
       
        }


        private void collectSlowQueries()
        {

            if (IsXESupported())
            {
                SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(_connectionString)
                {
                    ApplicationName = "DBADashXE"
                };
                string slowQueriesSQL;
                if (IsAzure)
                {
                    slowQueriesSQL = Properties.Resources.SQLSlowQueriesAzure;
                }
                else
                {
                    slowQueriesSQL = Properties.Resources.SQLSlowQueries;
                }
                using (var cn = new SqlConnection(builder.ConnectionString))
                {
                    using (var cmd = new SqlCommand(slowQueriesSQL, cn) { CommandTimeout = 90 })
                    {
                        cn.Open();

                        cmd.Parameters.AddWithValue("SlowQueryThreshold", SlowQueryThresholdMs * 1000);
                        cmd.Parameters.AddWithValue("MaxMemory", SlowQueryMaxMemoryKB);
                        cmd.Parameters.AddWithValue("UseDualSession", UseDualEventSession);
                        var result = cmd.ExecuteScalar();
                        if (result == DBNull.Value)
                        {
                            throw new Exception("Result is NULL");
                        }
                        string ringBuffer = (string)result;
                        if (ringBuffer.Length > 0)
                        {
                            var dt = XETools.XEStrToDT(ringBuffer, out RingBufferTargetAttributes ringBufferAtt);
                            dt.TableName = "SlowQueries";
                            addDT(dt);
                            var dtAtt = ringBufferAtt.GetTable();
                            dtAtt.TableName = "SlowQueriesStats";
                            addDT(dtAtt);

                        }
                    }
                }
            }
        }


        private void collectServerExtraProperties()
        {
            if (!this.IsAzure)
            {
                if (!noWMI)
                {
                    collectComputerSystemWMI();
                    collectOperatingSystemWMI();
                }
                addDT("ServerExtraProperties", DBADash.Properties.Resources.SQLServerExtraProperties);
                Data.Tables["ServerExtraProperties"].Columns.Add("WindowsCaption");
                if (manufacturer != "") { Data.Tables["ServerExtraProperties"].Rows[0]["SystemManufacturer"] = manufacturer; }
                if (model != "") { Data.Tables["ServerExtraProperties"].Rows[0]["SystemProductName"] = model; }
                Data.Tables["ServerExtraProperties"].Rows[0]["WindowsCaption"] = WindowsCaption;
                if (Data.Tables["ServerExtraProperties"].Rows[0]["ActivePowerPlanGUID"] == DBNull.Value && noWMI == false)
                {
                    collectPowerPlanWMI();
                    Data.Tables["ServerExtraProperties"].Rows[0]["ActivePowerPlanGUID"] = activePowerPlanGUID;
                    Data.Tables["ServerExtraProperties"].Rows[0]["ActivePowerPlan"] = activePowerPlan;
                }
            }
        }

        public DataTable getDT(string tableName, string SQL, SqlParameter[] param = null)
        {
            using (var cn = new SqlConnection(_connectionString))
            {
                using (var da = new SqlDataAdapter(SQL,cn)) {
                    cn.Open();
                    DataTable dt = new DataTable();
                    da.SelectCommand.CommandTimeout = 60;
                    if (param != null)
                    {
                        da.SelectCommand.Parameters.AddRange(param);
                    }
                    da.Fill(dt);
                    dt.TableName = tableName;
                    return dt;
                }

            }
        }

        public void addDT(string tableName, string sql, SqlParameter[] param = null)
        {
            if (!Data.Tables.Contains(tableName))
            {

                Data.Tables.Add(getDT(tableName, sql, param));

            }
        }

        private void addDT(DataTable dt)
        {
            if (!Data.Tables.Contains(dt.TableName))
            {
                Data.Tables.Add(dt);
            }
        }


        public void collectDrivesSQL()
        {
            try
            {
                addDT("Drives", Properties.Resources.SQLDrives);
            }
            catch (Exception ex)
            {
                logError(ex,"Drives");
            }
        }

        public void collectDrives()
        {

            if (noWMI)
            {
                collectDrivesSQL();
            }
            else
            {
                try
                {
                    collectDrivesWMI();
                }
                catch (Exception ex)
                {
                    logDBError("Drives", "Error collecting drives via WMI.  Drive info will be collected from SQL, but might be incomplete.  Use --nowmi switch to collect through SQL as default." + Environment.NewLine + ex.Message, "Collect:WMI");
                    Log.Warning(ex, "Error collecting drives via WMI.Drive info will be collected from SQL, but might be incomplete.Use--nowmi switch to collect through SQL as default.");
                    collectDrivesSQL();
                }
            }
        }

        string activePowerPlan;
        Guid activePowerPlanGUID;
        string manufacturer;
        string model;
        string WindowsCaption;

        #region "WMI"

        private void collectOperatingSystemWMI()
        {
            if (!noWMI)
            {
                try
                {
                    ManagementPath path = new ManagementPath()
                    {
                        NamespacePath = @"root\cimv2",
                        Server = computerName
                    };
                    ManagementScope scopeCIMV2 = new ManagementScope(path);

                    SelectQuery query = new SelectQuery("Win32_OperatingSystem", "", new string[] { "Caption" });
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scopeCIMV2, query))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        if (results.Count == 1)
                        {
                            var mo = results.OfType<ManagementObject>().FirstOrDefault();
                            if (mo != null)
                            {
                                WindowsCaption = getMOStringValue(mo, "Caption", 256);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logError(ex,"ServerExtraProperties","Collect:Win32_OperatingSystem WMI");
                }
            }
        }

        private void collectComputerSystemWMI()
        {
            if (!noWMI)
            {
                try
                {
                    ManagementPath path = new ManagementPath()
                    {
                        NamespacePath = @"root\cimv2",
                        Server = computerName
                    };
                    ManagementScope scopeCIMV2 = new ManagementScope(path);

                    SelectQuery query = new SelectQuery("Win32_ComputerSystem", "", new string[] { "Manufacturer", "Model" });
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scopeCIMV2, query))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        if (results.Count == 1)
                        {
                            var mo = results.OfType<ManagementObject>().FirstOrDefault();
                            if (mo != null)
                            {
                                manufacturer = getMOStringValue(mo, "Manufacturer", 200);
                                model = getMOStringValue(mo, "Model", 200);

                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logError(ex,"ServerExtraProperties", "Collect:Win32_ComputerSystem WMI");
                }
            }
        }

        private string getMOStringValue(ManagementObject mo, string propertyName, Int32 truncateLength = 0)
        {
            string value = "";
            if (mo.GetPropertyValue(propertyName) != null)
            {
                value = mo.GetPropertyValue(propertyName).ToString();
                if (truncateLength > 0 && value.Length > truncateLength)
                {
                    value = value.Substring(0, 200);
                }
            }
            return value;
        }

        private void collectPowerPlanWMI()
        {
            if (!noWMI)
            {
                try
                {
                    ManagementPath pathPower = new ManagementPath()
                    {
                        NamespacePath = @"root\cimv2\power",
                        Server = computerName
                    };
                    ManagementScope scopePower = new ManagementScope(pathPower);
                    SelectQuery query = new SelectQuery("Win32_PowerPlan", "IsActive=1", new string[] { "InstanceID", "ElementName" });
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scopePower, query))
                    using (ManagementObjectCollection results = searcher.Get())
                    {

                        var mo = results.OfType<ManagementObject>().FirstOrDefault();
                        if (mo != null)
                        {


                            string instanceId = getMOStringValue(mo, "InstanceID");
                            if (instanceId.Length > 0)
                            {
                                activePowerPlanGUID = Guid.Parse(instanceId.Substring(instanceId.Length - 38, 38));
                            }
                            activePowerPlan = getMOStringValue(mo, "ElementName");
                        }

                    }
                }
                catch (Exception ex)
                {
                    logError(ex,"ServerExtraProperties", "Collect:Win32_PowerPlan WMI");
                }
            }
        }

        private void collectDriversWMI()
        {
            if (!noWMI)
            {
                try
                {
                    if (!Data.Tables.Contains("Drivers"))
                    {
                        DataTable dtDrivers = new DataTable("Drivers");
                        string[] selectedProperties = new string[] { "ClassGuid", "DeviceClass", "DeviceID", "DeviceName", "DriverDate", "DriverProviderName", "DriverVersion", "FriendlyName", "HardWareID", "Manufacturer", "PDO" };
                        foreach (string p in selectedProperties)
                        {
                            if (p == "DriverDate")
                            {
                                dtDrivers.Columns.Add(p, typeof(DateTime));
                            }
                            else if (p == "ClassGuid")
                            {
                                dtDrivers.Columns.Add(p, typeof(Guid));
                            }
                            else
                            {
                                dtDrivers.Columns.Add(p, typeof(string));
                            }
                        }

                        ManagementPath path = new ManagementPath()
                        {
                            NamespacePath = @"root\cimv2",
                            Server = computerName
                        };
                        ManagementScope scope = new ManagementScope(path);

                        SelectQuery query = new SelectQuery("Win32_PnPSignedDriver", "", selectedProperties);

                        using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                        using (ManagementObjectCollection results = searcher.Get())
                        {
                            foreach (ManagementObject mo in results)
                            {
                                if (mo != null)
                                {
                                    var rDriver = dtDrivers.NewRow();
                                    foreach (string p in selectedProperties)
                                    {
                                        if (mo.GetPropertyValue(p) != null)
                                        {
                                            if (p == "DriverDate" || p == "InstallDate")
                                            {

                                                try
                                                {
                                                    rDriver[p] = ManagementDateTimeConverter.ToDateTime(mo.GetPropertyValue(p).ToString());
                                                }
                                                catch (Exception ex)
                                                {
                                                    logError(ex,"Drivers");
                                                }
                                            }

                                            else if (p == "ClassGuid")
                                            {
                                                try
                                                {
                                                    rDriver[p] = Guid.Parse(mo.GetPropertyValue(p).ToString());
                                                }
                                                catch (Exception ex)
                                                {
                                                    logError(ex,"Drivers");
                                                }

                                            }
                                            else
                                            {
                                                try
                                                {
                                                    string value = mo.GetPropertyValue(p).ToString();

                                                    rDriver[p] = value.Length <= 200 ? value : value.Substring(0, 200);

                                                }
                                                catch (Exception ex)
                                                {
                                                    logError(ex,"Drivers");
                                                }

                                            }

                                        }
                                    }
                                    dtDrivers.Rows.Add(rDriver);
                                }
                            }
                        }
                        try
                        {
                            var PVKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, computerName, RegistryView.Registry64).OpenSubKey("SOFTWARE\\Amazon\\PVDriver");
                            if (PVKey != null)
                            {
                                var rDriver = dtDrivers.NewRow();
                                rDriver["DeviceID"] = "HKEY_LOCAL_MACHINE\\SOFTWARE\\Amazon\\PVDriver";
                                rDriver["Manufacturer"] = "Amazon Inc.";
                                rDriver["DriverProviderName"] = "Amazon Inc.";
                                rDriver["DeviceName"] = "AWS PV Driver";
                                rDriver["DriverVersion"] = PVKey.GetValue("Version");
                                dtDrivers.Rows.Add(rDriver);
                            }
                        }
                        catch (Exception ex)
                        {
                            logError(ex,"Drivers", "Collect:AWSPVDriver");
                        }
                        Data.Tables.Add(dtDrivers);
                    }

                }
                catch (Exception ex)
                {
                    logError(ex,"Drivers","Collect:WMI");
                }
            }
        }

        private void collectDrivesWMI()
        {
            try
            {
                if (!Data.Tables.Contains("Drives"))
                {
                    DataTable drives = new DataTable("Drives");
                    drives.Columns.Add("Name", typeof(string));
                    drives.Columns.Add("Capacity", typeof(Int64));
                    drives.Columns.Add("FreeSpace", typeof(Int64));
                    drives.Columns.Add("Label", typeof(string));

                    ManagementPath path = new ManagementPath()
                    {
                        NamespacePath = @"root\cimv2",
                        Server = computerName
                    };
                    ManagementScope scope = new ManagementScope(path);
                    //string condition = "DriveLetter = 'C:'";
                    string[] selectedProperties = new string[] { "FreeSpace", "Name", "Capacity", "Caption", "Label" };
                    SelectQuery query = new SelectQuery("Win32_Volume", "DriveType=3 AND DriveLetter IS NOT NULL", selectedProperties);

                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject volume in results)
                        {


                            if (volume != null)
                            {
                                var rDrive = drives.NewRow();
                                rDrive["FreeSpace"] = (UInt64)volume.GetPropertyValue("FreeSpace");
                                rDrive["Name"] = (string)volume.GetPropertyValue("Name");
                                rDrive["Capacity"] = (UInt64)volume.GetPropertyValue("Capacity");
                                rDrive["Label"] = (string)volume.GetPropertyValue("Label");
                                drives.Rows.Add(rDrive);
                                // Use freeSpace here...
                            }
                        }
                    }

                    Data.Tables.Add(drives);

                }
            }
            catch (Exception ex)
            {
                logError(ex,"Drives", "Collect:WMI");
            }
        }

        #endregion

    }
}
