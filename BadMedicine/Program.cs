﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BadMedicine.Configuration;
using BadMedicine.Datasets;
using CommandLine;
using FAnsi.Discovery;
using FAnsi.Implementation;
using FAnsi.Implementations.MicrosoftSQL;
using FAnsi.Implementations.MySql;
using FAnsi.Implementations.Oracle;
using FAnsi.Implementations.PostgreSql;
using YamlDotNet.Serialization;

namespace BadMedicine
{
    class Program
    {
        private static int returnCode;
        public const string ConfigFile = "./BadMedicine.yaml";

        public static int Main(string[] args)
        {
            returnCode = 0;

            CommandLine.Parser.Default.ParseArguments<ProgramOptions>(args)
                .WithParsed<ProgramOptions>(opts => RunOptionsAndReturnExitCode(opts))
                .WithNotParsed<ProgramOptions>((errs) => HandleParseError(errs));


            return returnCode;
        }

        private static void HandleParseError(IEnumerable<Error> errs)
        {
            returnCode = 500;
        }

        private static void RunOptionsAndReturnExitCode(ProgramOptions opts)
        {

            if (opts.NumberOfPatients <= 0)
                opts.NumberOfPatients = 500;
            if (opts.NumberOfRows <= 0)
                opts.NumberOfRows = 2000;

            Config config = null;

            if (File.Exists(ConfigFile))
            {
                try
                {
                    var d = new Deserializer();
                    config = d.Deserialize<Config>(File.ReadAllText(ConfigFile));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error deserializing '{ConfigFile}'");
                    Console.Write(e.ToString());
                    returnCode = -1;
                    return;
                }
            }


            var dir = Directory.CreateDirectory(opts.OutputDirectory);

            try
            {
                //if user wants to write out the lookups generate those too
                if(opts.Lookups)
                    DataGenerator.WriteLookups(dir);

                Random r = opts.Seed == -1 ? new Random() : new Random(opts.Seed);

                //create a cohort of people
                IPersonCollection identifiers = new PersonCollection();
                identifiers.GeneratePeople(opts.NumberOfPatients,r);

                var factory = new DataGeneratorFactory();
                var generators = factory.GetAvailableGenerators().ToList();
            
                //if the user only wants to extract a single dataset
                if(!string.IsNullOrEmpty(opts.Dataset))
                {
                    var match = generators.FirstOrDefault(g => g.Name.Equals(opts.Dataset));
                    if(match == null)
                    {
                        Console.WriteLine("Could not find dataset called '" + opts.Dataset + "'");
                        Console.WriteLine("Generators found were:" + Environment.NewLine + string.Join(Environment.NewLine,generators.Select(g=>g.Name)));
                        returnCode = 2;
                        return;
                    }

                    generators = new List<Type>(new []{match});
                }

                // if we are not going to write out to a database
                if (config?.Database != null)
                {
                    try
                    {
                        //for each generator
                        foreach (var g in generators)
                        {
                            var instance = factory.Create(g, r);
                            returnCode = Math.Min(RunDatabaseTarget(identifiers,config.Database, instance,opts.NumberOfRows),returnCode);
                        }

                        return;
                    }
                    catch (Exception e)
                    {

                        Console.WriteLine(e);
                        returnCode = 3;
                        return;
                    }
                }
                else
                {
                    // we are writting out to CSV

                    //for each generator
                    foreach (var g in generators)
                    {
                        var instance = factory.Create(g, r);

                        var targetFile = new FileInfo(Path.Combine(dir.FullName, g.Name + ".csv"));
                        instance.GenerateTestDataFile(identifiers, targetFile, opts.NumberOfRows);
                    }
                }
            
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                returnCode = 2;
                return;
            }

            returnCode = 0;
        }

        private static int RunDatabaseTarget(IPersonCollection cohort,TargetDatabase configDatabase, IDataGenerator generator, int numberOfRows)
        {

            ImplementationManager.Load<MySqlImplementation>();
            ImplementationManager.Load<PostgreSqlImplementation>();
            ImplementationManager.Load<OracleImplementation>();
            ImplementationManager.Load<MicrosoftSQLImplementation>();

            var server = new DiscoveredServer(configDatabase.ConnectionString, configDatabase.DatabaseType);

            try
            {
                server.TestConnection(10000);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not reach target server '{server.Name}'");
                Console.WriteLine(e);
                return -2;
            }


            var db = server.ExpectDatabase(configDatabase.DatabaseName);

            if (!db.Exists())
            {
                Console.WriteLine($"Creating Database '{db.GetRuntimeName()}'");
                db.Create();
                Console.WriteLine("Database Created");
            }
            else
            {
                Console.WriteLine($"Found Database '{db.GetRuntimeName()}'");
            }

            var tblName = generator.GetType().Name;

            if(db.ExpectTable(tblName).Exists())
            {

                Console.WriteLine($"Found Existing Table '{tblName}'");

                if(configDatabase.DropTables)
                    db.ExpectTable(tblName).Drop();
                else
                {
                    Console.WriteLine("Skipping table because it already existed and DropTables is false");
                    return -3;
                }
            }

            Console.WriteLine($"Creating Table '{tblName}'");

            var dt = generator.GetDataTable(cohort, numberOfRows);
            var tbl = db.CreateTable(tblName, dt);

            Console.WriteLine($"Finished Creating '{tblName}'.  It had '{tbl.GetRowCount()}' rows");
            return 0;
        }
    }
}
