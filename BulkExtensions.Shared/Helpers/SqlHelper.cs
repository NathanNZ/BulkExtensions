﻿using System;
using System.Collections.Generic;
using System.Linq;
#if NETSTANDARD1_3
using System.Reflection;
#endif
using System.Text;
using EntityFramework.BulkExtensions.Commons.Context;
using EntityFramework.BulkExtensions.Commons.Extensions;
using EntityFramework.BulkExtensions.Commons.Flags;
using EntityFramework.BulkExtensions.Commons.Mapping;

namespace EntityFramework.BulkExtensions.Commons.Helpers
{
    internal static class SqlHelper
    {
        private const string Source = "Source";
        private const string Target = "Target";
        internal const string Identity = "Bulk_Identity";

        /// <summary>
        ///
        /// </summary>
        /// <param name="mapping"></param>
        /// <returns></returns>
        internal static string RandomTableName(this IEntityMapping mapping)
        {
            var schema = string.IsNullOrEmpty(mapping.Schema?.Trim())
                ? string.Empty
                : $"[{mapping.Schema}].";
            return $"{schema}[_{mapping.TableName}_{GuidHelper.GetRandomTableGuid()}]";
        }

        /// <summary>
        /// </summary>
        /// <param name="mapping"></param>
        /// <param name="tableName"></param>
        /// <param name="operationType"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        internal static string CreateTempTable(this IEntityMapping mapping, string tableName, Operation operationType,
            BulkOptions options)
        {
            var paramList = mapping.Properties
                .FilterPropertiesByOperation(operationType)
                .Select(column => $"{Source}.[{column.ColumnName}]")
                .ToList();

            if (options.HasFlag(BulkOptions.OutputIdentity) && mapping.HasStoreGeneratedKey)
            {
                paramList.Add($"1 as [{Identity}]");
            }

            var paramListConcatenated = string.Join(", ", paramList);

            return $"SELECT TOP 0 {paramListConcatenated} INTO {tableName} FROM {mapping.FullTableName} AS A " +
                   $"LEFT JOIN {mapping.FullTableName} AS {Source} ON 1 = 2";
        }

        internal static string BuildMergeCommand(this IDbContextWrapper context, string tmpTableName,
            Operation operationType)
        {
            var command = $"MERGE INTO {context.EntityMapping.FullTableName} WITH (HOLDLOCK) AS {Target} USING {tmpTableName} AS {Source} " +
                $"{context.EntityMapping.PrimaryKeysComparator()} ";

            switch (operationType)
            {
                case Operation.Insert:
                    command += context.EntityMapping.BuildMergeInsertSet();
                    break;
                case Operation.Update:
                    command += context.EntityMapping.BuildMergeUpdateSet();
                    command += GetDropTableCommand(tmpTableName);
                    break;
                case Operation.InsertOrUpdate:
                    command += context.EntityMapping.BuildMergeUpdateSet();
                    command += context.EntityMapping.BuildMergeInsertSet();
                    break;
                case Operation.Delete:
                    command += "WHEN MATCHED THEN DELETE";
                    command += GetDropTableCommand(tmpTableName);
                    break;
            }

            return command;
        }

        /// <summary>
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="context"></param>
        /// <param name="outputTableName"></param>
        /// <param name="propertyMapping"></param>
        /// <param name="items"></param>
        internal static void LoadFromTmpOutputTable<TEntity>(this IDbContextWrapper context, string outputTableName,
            IPropertyMapping propertyMapping, IList<TEntity> items)
        {
            var command = $"SELECT {Identity}, {propertyMapping.ColumnName} FROM {outputTableName}";

            using (var reader = context.SqlQuery(command))
            {
                while (reader.Read())
                {
                    var item = items.ElementAt((int)reader[Identity]);

                    var property = item.GetType().GetProperty(propertyMapping.PropertyName);

                    if (property != null && property.CanWrite)
                        property.SetValue(item, reader[propertyMapping.ColumnName], null);

                    else
                        throw new Exception();
                }
            }

            command = GetDropTableCommand(outputTableName);
            context.ExecuteSqlCommand(command);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tableName"></param>
        /// <returns></returns>
        internal static string GetDropTableCommand(string tableName)
        {
            return $"; DROP TABLE {tableName};";
        }

        /// <summary>
        /// </summary>
        /// <param name="mapping"></param>
        /// <returns></returns>
        private static string BuildMergeUpdateSet(this IEntityMapping mapping)
        {
            var command = new StringBuilder();
            var parameters = new List<string>();

            command.Append("WHEN MATCHED THEN UPDATE SET ");

            foreach (var column in mapping.Properties)
            {
                if (column.IsStoreGenerated || column.IsHierarchyMapping) continue;

                parameters.Add($"[{Target}].[{column.ColumnName}] = [{Source}].[{column.ColumnName}]");
            }

            command.Append(string.Join(", ", parameters) + " ");

            return command.ToString();
        }

        private static string BuildMergeInsertSet(this IEntityMapping mapping)
        {
            var command = new StringBuilder();
            var columns = new List<string>();
            var values = new List<string>();

            command.Append(" WHEN NOT MATCHED BY TARGET THEN INSERT (");

            foreach (var column in mapping.Properties)
            {
                if (column.IsStoreGenerated) continue;

                columns.Add($"[{column.ColumnName}]");
                values.Add($"[{Source}].[{column.ColumnName}]");
            }

            command.Append(string.Join(", ", columns));
            command.Append(") VALUES (");
            command.Append(string.Join(", ", values));
            command.Append(")");

            return command.ToString();
        }

        internal static string BuildOutputId(string outputTableName, string identityColumn)
        {
            return $" OUTPUT {Source}.{Identity}, INSERTED.{identityColumn} INTO {outputTableName} ({Identity}, {identityColumn})";
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="mapping"></param>
        /// <returns></returns>
        private static string PrimaryKeysComparator(this IEntityMapping mapping)
        {
            var keys = mapping.Pks.ToList();
            var command = new StringBuilder();
            var firstKey = keys.First();

            command.Append($"ON [{Target}].[{firstKey.ColumnName}] = [{Source}].[{firstKey.ColumnName}] ");
            keys.Remove(firstKey);

            if (keys.Any())
                foreach (var key in keys)
                    command.Append($"AND [{Target}].[{key.ColumnName}] = [{Source}].[{key.ColumnName}]");

            return command.ToString();
        }

        internal static string CreateOutputTableCmd(string tmpTablename, string identityColumn, Operation operationType)
        {
            return $"CREATE TABLE {tmpTablename} ([{Identity}] int, [{identityColumn}] int);";
        }
    }
}