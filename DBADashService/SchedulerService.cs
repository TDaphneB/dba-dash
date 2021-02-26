﻿using DBADash;
using Newtonsoft.Json;
using Quartz;
using Quartz.Impl;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using static DBADash.DBADashConnection;

namespace DBADashService
{

    public class ScheduleService
    {
        private readonly IScheduler scheduler;
        public readonly CollectionConfig config;
        System.Timers.Timer azureScanForNewDBsTimer;


        public  ScheduleService()
        {
            config = SchedulerServiceConfig.Config;

            Int32 threads = config.ServiceThreads;
            if (threads < 1)
            {
                threads = 10;
                Console.WriteLine("Threads:" + threads + " (default)");
            }
            else
            {
                Console.WriteLine("Threads:" + threads + "(user)");
            }
            
            NameValueCollection props = new NameValueCollection
        {
            { "quartz.serializer.type", "binary" },
            { "quartz.scheduler.instanceName", "DBADashScheduler" },
            { "quartz.jobStore.type", "Quartz.Simpl.RAMJobStore, Quartz" },
            { "quartz.threadPool.threadCount", threads.ToString() }
        };
            
            StdSchedulerFactory factory = new StdSchedulerFactory(props);
            scheduler = factory.GetScheduler().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public static void ErrorLogger(Exception ex, string context)
        {
            Console.WriteLine(context + ": " + ex.Message);
            try
            {
                EventLog.WriteEntry("DBADashService", context + ": " + ex.Message, EventLogEntryType.Error);
            }
            catch(Exception ex2)
            {
                Console.WriteLine("Unable to write error to eventlog: " + ex2.Message + Environment.NewLine + ex.Message);
            }
        }


        private void removeEventSessions(CollectionConfig config)
        {   
            try
            {
                Parallel.ForEach(config.SourceConnections, cfg => {
                    if (cfg.SourceConnection.Type == ConnectionType.SQL)
                    {
                        try
                        {
                            var collector = new DBCollector(cfg.GetSource(), cfg.NoWMI);
                            if (cfg.PersistXESessions)
                            {
                                Console.WriteLine("Stop DBADash event sessions: " + cfg.SourceConnection.ConnectionForPrint);
                                collector.StopEventSessions();
                            }
                            else
                            {
                                Console.WriteLine("Remove DBADash event sessions: " + cfg.SourceConnection.ConnectionForPrint);
                                collector.RemoveEventSessions();
                            }
                        }
                        catch(Exception ex)
                        {
                            ErrorLogger(ex, "Stop/Remove DBADash Event Sessions:" + cfg.SourceConnection.ConnectionForPrint);
                        }

                    }
                });
            }
            catch (Exception ex)
            {
                ErrorLogger(ex, "Remove Event Sessions");
            }
        }

        private void upgradeDB()
        {
            foreach (var d in config.AllDestinations.Where(dest => dest.Type == ConnectionType.SQL))
            {
                Console.WriteLine("Version check " + d.ConnectionForPrint);
                var status = DBValidations.VersionStatus(d.ConnectionString);
                if (status.VersionStatus == DBValidations.DBVersionStatusEnum.AppUpgradeRequired)
                {
                    ErrorLogger(new Exception("Warning: This version of the app is older than the repository DB and should be upgraded"), "DB Version Check");
                }
                else if (status.VersionStatus == DBValidations.DBVersionStatusEnum.CreateDB)
                {
                    if (config.AutoUpdateDatabase)
                    {
                        Console.WriteLine("Create repository DB...");
                        DBValidations.UpgradeDBAsync(d.ConnectionString).Wait();
                        Console.WriteLine("Repository DB created");
                    }
                    else
                    {
                        throw new Exception("Repository database needs to be created.  Use to service configuration tool to deploy the repository database.");
                    }
                }
                else if (status.VersionStatus == DBValidations.DBVersionStatusEnum.UpgradeRequired)
                {
                    if (config.AutoUpdateDatabase)
                    {
                        Console.WriteLine(string.Format("Upgrade DB from {0} to {1}", status.DBVersion.ToString(), status.DACVersion.ToString()));
                        DBValidations.UpgradeDBAsync(d.ConnectionString).Wait();
                        status = DBValidations.VersionStatus(d.ConnectionString);
                        if (status.VersionStatus == DBValidations.DBVersionStatusEnum.OK)
                        {
                            Console.WriteLine("Upgrade completed");
                        }
                        else
                        {
                            throw new Exception(string.Format("Database version is {0} is not expected following upgrade to {1}", status.DBVersion.ToString(), status.DACVersion.ToString()));
                        }
                    }
                    else
                    {
                        throw new Exception("Database upgrade is required.  Enable auto updates or run the service configuration tool to update.");
                    }
                }
                else if (status.VersionStatus == DBValidations.DBVersionStatusEnum.OK)
                {
                    Console.WriteLine("Version check passed: " + status.DBVersion.ToString());
                }

            }
        }


        public void Start()
        {
            scheduler.Start().ConfigureAwait(false).GetAwaiter().GetResult();
            upgradeDB();
            ScheduleJobs();
        }




        public void ScheduleJobs()
        {
            Console.WriteLine("Agent Version:" + Assembly.GetEntryAssembly().GetName().Version);

            if (config.ScanForAzureDBs)
            {
                ScanForAzureDBs();
                if (config.ScanForAzureDBsInterval > 0)
                {
                    Console.WriteLine($"Scan for new Azure DBS every {config.ScanForAzureDBsInterval} seconds");
                    azureScanForNewDBsTimer = new System.Timers.Timer
                    {
                        Enabled = true,
                        Interval = config.ScanForAzureDBsInterval * 1000
                    };
                    azureScanForNewDBsTimer.Elapsed += new System.Timers.ElapsedEventHandler(ScanForAzureDBs);
                }
            }

            removeEventSessions(config);

            Int32 i = 0;
            foreach(DBADashConnection d in config.AllDestinations.Where(cn => cn.Type== ConnectionType.SQL))
            {
                i += 1;
                string maintenanceCron = config.GetMaintenanceCron();

                IJobDetail job = JobBuilder.Create<MaintenanceJob>()
                        .WithIdentity("MaintenanceJob" + i.ToString())
                        .UsingJobData("ConnectionString", d.ConnectionString)
                        .Build();
                ITrigger trigger = TriggerBuilder.Create()
                .StartNow()
                .WithCronSchedule(maintenanceCron)
                .Build();
             
                scheduler.ScheduleJob(job, trigger).ConfigureAwait(false).GetAwaiter().GetResult();
                scheduler.TriggerJob(job.Key);

            }
            scheduleSourceCollection(config.SourceConnections);

        }

        private void scheduleSourceCollection(List<DBADashSource> sourceConnections)
        {
            foreach (DBADashSource cfg in sourceConnections)
            {
                string cfgString = JsonConvert.SerializeObject(cfg);

                foreach (var s in cfg.GetSchedule())
                {
                    IJobDetail job = JobBuilder.Create<DBADashJob>()
                           .UsingJobData("Type", JsonConvert.SerializeObject(s.CollectionTypes))
                           .UsingJobData("Source", cfg.SourceConnection.ConnectionString)
                           .UsingJobData("CFG", cfgString)
                           .UsingJobData("SourceType", JsonConvert.SerializeObject(cfg.SourceConnection.Type))
                          .Build();
                    ITrigger trigger = TriggerBuilder.Create()
                    .StartNow()
                    .WithCronSchedule(s.CronSchedule)
                    .Build();

                    scheduler.ScheduleJob(job, trigger).ConfigureAwait(false).GetAwaiter().GetResult();
                    if (s.RunOnServiceStart)
                    {
                        scheduler.TriggerJob(job.Key);
                    }

                }
                if (cfg.SchemaSnapshotDBs != null && cfg.SchemaSnapshotDBs.Length > 0)
                {
                    IJobDetail job = JobBuilder.Create<SchemaSnapshotJob>()
                          .UsingJobData("Source", cfg.SourceConnection.ConnectionString)
                          .UsingJobData("CFG", cfgString)
                          .UsingJobData("SchemaSnapshotDBs", cfg.SchemaSnapshotDBs)
                             .Build();
                    ITrigger trigger = TriggerBuilder.Create()
                      .StartNow()
                      .WithCronSchedule(cfg.SchemaSnapshotCron)
                      .Build();


                    scheduler.ScheduleJob(job, trigger).ConfigureAwait(false).GetAwaiter().GetResult();
                    if (cfg.SchemaSnapshotOnServiceStart)
                    {
                        scheduler.TriggerJob(job.Key);
                    }

                }
            }
        }

        private void ScanForAzureDBs()
        {
            Console.WriteLine("Scan for AzureDBs...");
            scheduleSourceCollection(config.AddAzureDBs());
        }

        private void ScanForAzureDBs(object sender, ElapsedEventArgs e)
        {
            ScanForAzureDBs();
        }

        public void Stop()
        {
            removeEventSessions(config);
            scheduler.Shutdown().ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }
}
