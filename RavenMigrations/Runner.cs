﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Raven.Client;

namespace RavenMigrations
{
    public class Runner
    {
        public static void Run(IDocumentStore documentStore, MigrationOptions options = null)
        {
            if (options == null)
                options = new MigrationOptions();

            if (!options.Assemblies.Any())
                options.Assemblies.Add(Assembly.GetCallingAssembly());

            var migrations = FindAllMigrationsWithOptions(options);

            foreach (var pair in migrations)
            {
                // send in the document Store
                var migration = pair.Migration();
                migration.Setup(documentStore, options.Logger);

                // todo: possible issue here with sharding
                var migrationId = 
                    migration.GetMigrationIdFromName(documentStore.Conventions.IdentityPartsSeparator[0]);

                using (var session = documentStore.OpenSession())
                {
                    var migrationDoc = session.Load<MigrationDocument>(migrationId);

                    switch (options.Direction)
                    {
                        case Directions.Down:
                            options.Logger.WriteInformation("{0}: Down migration started", migration.GetType().Name);
                            migration.Down();
                            session.Delete(migrationDoc);
                            options.Logger.WriteInformation("{0}: Down migration completed", migration.GetType().Name);
                            break;
                        default:
                            // we already ran it
                            if (migrationDoc != null)
                                continue;

                            options.Logger.WriteInformation("{0}: Up migration started", migration.GetType().Name);
                            migration.Up();
                            session.Store(new MigrationDocument { Id = migrationId });
                            options.Logger.WriteInformation("{0}: Up migration completed", migration.GetType().Name);
                            break;
                    }

                    session.SaveChanges();

                    if (pair.Attribute.Version == options.ToVersion)
                        break;
                }
            }
        }

        /// <summary>
        /// Returns all migrations found within all assemblies and orders them by the direction
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static IEnumerable<MigrationWithAttribute> FindAllMigrationsWithOptions(MigrationOptions options)
        {
            var migrationsToRun = 
                from assembly in options.Assemblies
                from t in assembly.GetLoadableTypes()
                where typeof(Migration).IsAssignableFrom(t)
                select new MigrationWithAttribute
                {
                    Migration = () => options.MigrationResolver.Resolve(t),
                    Attribute = t.GetMigrationAttribute()
                } into migration
                where migration.Attribute != null && IsInCurrentMigrationProfile(migration, options)
                select migration;

            // if we are going down, we want to run it in reverse
            return options.Direction == Directions.Down 
                ? migrationsToRun.OrderByDescending(x => x.Attribute.Version) 
                : migrationsToRun.OrderBy(x => x.Attribute.Version);
        }

        private static bool IsInCurrentMigrationProfile(MigrationWithAttribute migrationWithAttribute, MigrationOptions options)
        {
            //If no particular profiles have been set, then the migration is
            //effectively a part of all profiles
            var profiles = migrationWithAttribute.Attribute.Profiles;
            if (profiles.Any() == false)
                return true;

            //The migration must belong to at least one of the currently 
            //specified profiles
            return options.Profiles
                .Intersect(migrationWithAttribute.Attribute.Profiles, StringComparer.OrdinalIgnoreCase)
                .Any();
        }
    }
}
