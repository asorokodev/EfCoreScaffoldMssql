﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EfCoreScaffoldMssql.Classes;
using EfCoreScaffoldMssql.Helpers;
using HandlebarsDotNet;

namespace EfCoreScaffoldMssql
{
    public class Scaffolder
    {
        private static readonly Regex RemoveIdRegex = new Regex("(?<content>.+)(Id)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly ScaffoldOptions _options;

        public Scaffolder(ScaffoldOptions options)
        {
            _options = options;
        }

        private void WriteLine(string message)
        {
            if (_options.IsVerbose)
            {
                Console.WriteLine(message);
            }
        }

        private void AddDependencies(EntityViewModel entity, List<ColumnDefinition> columns)
        {
            if (columns.Any(x => x.TypeName == "geometry"))
            {
                entity.Dependencies.Add(new Dependency
                {
                    Name = "NetTopologySuite.Geometries"
                });
            }
        }

        public void ScaffoldEntities(
            List<EntityViewModel> entities,
            List<EntityDefinition> objects, 
            List<ColumnDefinition> columns, 
            List<KeyColumnDefinition> keyColumns,
            List<FkDefinition> fkDefinitions,
            List<string> ignoreObjects,
            string defaultSchemaName)
        {
            keyColumns = keyColumns ?? new List<KeyColumnDefinition>();
            fkDefinitions = fkDefinitions ?? new List<FkDefinition>();

            foreach (var table in objects.OrderBy(x => x.EntityName))
            {
                if (_options.Schemas.Any() && !_options.Schemas.Contains(table.SchemaName.ToLower()))
                    continue;

                if (ignoreObjects.Contains($"[{table.SchemaName}].[{table.EntityName}]".ToLower()))
                    continue;

                var entityViewModel = table.CloneCopy<EntityDefinition, EntityViewModel>();

                entityViewModel.Namespace = _options.Namespace;

                var tableColumns = columns
                    .Where(x => x.SchemaName == table.SchemaName && x.ObjectName == table.EntityName)
                    .ToList();

                var tableKeys = keyColumns
                    .Where(x => x.TableSchema == table.SchemaName && x.TableName == table.EntityName)
                    .OrderBy(x => x.KeyOrder)
                    .ToList();

                var tableFks = fkDefinitions
                    .Where(x => x.FkSchema == table.SchemaName && x.FkTable == table.EntityName)
                    .ToList();

                entityViewModel.Keys = tableKeys;
                entityViewModel.IsDefaultSchema = defaultSchemaName == entityViewModel.SchemaName;

                AddDependencies(entityViewModel, tableColumns);

                foreach (var tableColumn in tableColumns.OrderBy(x => x.ColumnId))
                {
                    var columnViewModel = tableColumn.CloneCopy<ColumnDefinition, ColumnViewModel>();

                    var keyIndex = tableKeys.FindIndex(x => x.ColumnName == tableColumn.Name);
                    columnViewModel.IsKey = keyIndex > -1;
                    columnViewModel.KeyColumnNumber = keyIndex + 1;

                    var hasFkDefinition = tableFks.Any(x => x.FkColumns.Contains(tableColumn.Name));
                    columnViewModel.IsPartOfForeignKey = hasFkDefinition;

                    entityViewModel.Columns.Add(columnViewModel);
                }

                entities.Add(entityViewModel);
            }
        }

        public void Generate()
        {
            const string setFileName = "set.hbs";
            const string contextFileName = "context.hbs";
            Func<object, string> templateSet;
            Func<object, string> templateContext;
            try
            {
                var setTemplate = File.ReadAllText(Path.Combine(_options.TemplatesDirectory, setFileName));
                templateSet = Handlebars.Compile(setTemplate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error compiling Handlebars template {setFileName}: {ex.Message}");
                return;
            }
            try
            {
                var tableContext = File.ReadAllText(Path.Combine(_options.TemplatesDirectory, contextFileName));
                templateContext = Handlebars.Compile(tableContext);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error compiling Handlebars template {contextFileName}: {ex.Message}");
                return;
            }
            
            WriteLine("Templates are ready");

            using (var connection = new SqlConnection(_options.ConnectionString))
            {
                connection.Open();
                WriteLine("Connected to the database");

                var fkDefinitionsSource = connection.ReadObjects<FkDefinitionSource>(SchemaSql.ForeignKeysSql);
                var fkDefinitions =
                    (from s in fkDefinitionsSource
                    group s by new {s.PkSchema, s.FkSchema, s.FkTable, s.PkTable, s.FkName, s.PkName, s.MatchOption, s.UpdateRule, s.DeleteRule}
                    into sGroup
                    select new FkDefinition
                    {
                        PkSchema = sGroup.Key.PkSchema,
                        FkSchema = sGroup.Key.FkSchema,
                        PkTable = sGroup.Key.PkTable,
                        FkTable = sGroup.Key.FkTable,
                        PkName = sGroup.Key.PkName,
                        FkName = sGroup.Key.FkName,
                        MatchOption = sGroup.Key.MatchOption,
                        DeleteRule = sGroup.Key.DeleteRule,
                        UpdateRule = sGroup.Key.UpdateRule,
                        PkColumns = sGroup.OrderBy(x => x.PkOrdinalPosition).Select(x => x.PkColumn).ToList(),
                        FkColumns = sGroup.OrderBy(x => x.FkOrdinalPosition).Select(x => x.FkColumn).ToList()
                    }).ToList();

                WriteLine("Foreign keys information received");

                var keyColumns = connection.ReadObjects<KeyColumnDefinition>(SchemaSql.KeyColumnsSql);
                WriteLine("Primary keys information received");

                var tables = connection.ReadObjects<EntityDefinition>(SchemaSql.TablesSql);
                WriteLine("Tables information received");

                var views = connection.ReadObjects<EntityDefinition>(SchemaSql.ViewsSql);
                WriteLine("Views information received");

                var tablesColumns = connection.ReadObjects<ColumnDefinition>(string.Format(SchemaSql.TableColumnsSql, _options.ExtendedPropertyTypeName));
                WriteLine("Tables columns information received");

                var viewsColumns = connection.ReadObjects<ColumnDefinition>(string.Format(SchemaSql.ViewColumnsSql, _options.ExtendedPropertyTypeName));
                WriteLine("Views columns information received");

                var spDefinitions = new List<StoredObjectDefinition>();
                if (_options.GenerateStoredProcedures)
                {
                    spDefinitions = GetStoredObjectsDefinition(connection, SchemaSql.StoredProcedureParametersSql, false);
                    WriteLine("Stored procedures parameters information received");

                    foreach (var sp in spDefinitions)
                    {
                        WriteLine($"Reading schema for {sp.Schema}.{sp.Name}");
                        var spSetDefinition = string.Format(SchemaSql.StoredProcedureSetSql, sp.Schema, sp.Name);
                        var columns = connection.ReadObjects<StoredObjectSetColumn>(spSetDefinition);
                        sp.Columns = columns;
                    }
                }

                var tvfDefinitions = new List<StoredObjectDefinition>();

                if (_options.GenerateTableValuedFunctions)
                {
                    tvfDefinitions = GetStoredObjectsDefinition(connection, SchemaSql.TableValueFunctionParametersSql, true);
                    WriteLine("Table valued functions parameters information received");

                    var tvfColumns = connection.ReadObjects<TableValuedColumn>(SchemaSql.TableValueFunctionColumnsSql);
                    WriteLine("Table valued functions parameters information received");
                    foreach (var tvf in tvfDefinitions)
                    {
                        tvf.Columns = tvfColumns
                            .Where(c => c.Schema == tvf.Schema && c.FunctionName == tvf.Name)
                            .Cast<StoredObjectSetColumn>().ToList();
                    }
                }

                var defaultSchemaName = connection.ReadObjects<SchemaDefinition>(SchemaSql.DefaultSchemaSql).First().SchemaName;

                var entityViewModels = new List<EntityViewModel>();

                ScaffoldEntities(entityViewModels, tables, tablesColumns, keyColumns, fkDefinitions, _options.IgnoreTables, defaultSchemaName);

                ScaffoldEntities(entityViewModels, views, viewsColumns, null, null, _options.IgnoreViews, defaultSchemaName);

                var pKeys =
                    (from pk in keyColumns
                        group pk by new {pk.TableSchema, pk.TableName, pk.KeyName}
                        into pkGroup
                        select new
                        {
                            pkGroup.Key.TableSchema,
                            pkGroup.Key.TableName,
                            Columns = pkGroup.OrderBy(x => x.KeyOrder).Select(x => x.ColumnName).ToList()
                        }).ToDictionary(x => $"{x.TableSchema}.{x.TableName}", x => x.Columns);

                foreach (var foreignKey in fkDefinitions)
                {
                    
                    var originTable = entityViewModels.SingleOrDefault(x =>
                        x.SchemaName == foreignKey.PkSchema && x.EntityName == foreignKey.PkTable);

                    var foreignTable = entityViewModels.SingleOrDefault(x =>
                        x.SchemaName == foreignKey.FkSchema && x.EntityName == foreignKey.FkTable);

                    var isOneToOne = false;
                    //Check one-2-one in case matched columns names and theirs orders
                    var originTableFullName = $"{foreignKey.PkSchema}.{foreignKey.PkTable}";
                    var foreignTableFullName = $"{foreignKey.FkSchema}.{foreignKey.FkTable}";
                    if (pKeys.ContainsKey(originTableFullName) && pKeys.ContainsKey(foreignTableFullName))
                    {
                        var pKeyOrigin = pKeys[originTableFullName];
                        var pKeyForeign = pKeys[foreignTableFullName];
                        if (foreignKey.PkColumns.Count == foreignKey.FkColumns.Count && foreignKey.PkColumns.Count == pKeyOrigin.Count && foreignKey.PkColumns.Count == pKeyForeign.Count)
                        {
                            isOneToOne = true;
                            for (var i = 0; i < pKeyOrigin.Count; i++)
                            {
                                if (pKeyOrigin[i] == foreignKey.PkColumns[i] && pKeyForeign[i] == foreignKey.FkColumns[i])
                                {
                                    continue;
                                }
                                isOneToOne = false;
                                break;
                            }
                        }
                    }

                    if (originTable != null && foreignTable != null)
                    {
                        var propertyName = string.Empty;
                        foreach (var fkColumn in foreignKey.FkColumns)
                        {
                            propertyName = RemoveIdRegex.Replace(fkColumn, m => m.Groups["content"].Value).TrimEnd('_');
                        }

                        if (_options.ForeignPropertyRegex != null)
                        {
                            propertyName = Regex.Match(foreignKey.FkName, _options.ForeignPropertyRegex, RegexOptions.Singleline).Groups["PropertyName"].Value;
                            propertyName = propertyName.Replace("_", string.Empty);
                            if (propertyName.EndsWith("Id") || propertyName.EndsWith("ID"))
                            {
                                propertyName = propertyName.Substring(0, propertyName.Length - 2);
                            }
                        }

                        var inversePropertyName = propertyName.ReplaceFirstOccurrance(originTable.EntityName, foreignTable.EntityName);
                        if (!isOneToOne)
                        {
                            inversePropertyName = StringHelper.Pluralize(inversePropertyName);
                        }

                        if (originTable == foreignTable)
                        {
                            inversePropertyName = "Inverse" + propertyName;
                        }

                        var foreignKeyViewModel = foreignKey.CloneCopy<FkDefinition, ForeignKeyViewModel>();
                        foreignKeyViewModel.PropertyName = propertyName;
                        foreignKeyViewModel.InversePropertyName = inversePropertyName;
                        foreignKeyViewModel.InverseEntityName = originTable.EntityName;
                        foreignKeyViewModel.IsOneToOne = isOneToOne;
                        foreignTable.ForeignKeys.Add(foreignKeyViewModel);

                        var inverseKeyViewModel = foreignKey.CloneCopy<FkDefinition, ForeignKeyViewModel>();
                        inverseKeyViewModel.PropertyName = inversePropertyName;
                        inverseKeyViewModel.InversePropertyName = propertyName;
                        foreignKeyViewModel.InverseEntityName = foreignTable.EntityName;
                        inverseKeyViewModel.IsOneToOne = isOneToOne;
                        originTable.InverseKeys.Add(inverseKeyViewModel);
                    }
                }

                var fileNames = new List<string>();
                var modelsDirectory = Path.Combine(_options.Directory, _options.ModelsPath);
                Directory.CreateDirectory(modelsDirectory);

                foreach (var tableViewModel in entityViewModels)
                {
                    var setResult = templateSet(tableViewModel);
                    var setResultFileName = Path.Combine(modelsDirectory, tableViewModel.EntityName + ".cs");
                    File.WriteAllText(setResultFileName, setResult);

                    fileNames.Add(setResultFileName);
                }

                if (_options.GenerateStoredProcedures)
                {
                    foreach (var p in spDefinitions.Where(x => x.Columns.Count > 0))
                    {
                        var model = new EntityViewModel
                        {
                            SchemaName = p.Schema,
                            EntityName = p.ResultTypeName,
                            Namespace = _options.Namespace,
                            IsVirtual = true,
                            Columns = p.Columns.Select(c => new ColumnViewModel
                            {
                                SchemaName = p.Schema,
                                Name = c.Name,
                                TypeName = c.SqlType,
                                IsNullable = c.IsNullable
                            }).ToList()
                        };

                        var setResult = templateSet(model);
                        var setResultFileName = Path.Combine(modelsDirectory, p.ResultTypeName + ".cs");
                        File.WriteAllText(setResultFileName, setResult);

                        fileNames.Add(setResultFileName);
                    }
                }

                var contextViewModel = new ContextViewModel
                {
                    ContextName = _options.ContextName,
                    Namespace = _options.Namespace,
                    Entities = entityViewModels,
                    StoredProcedures = spDefinitions,
                    TableValuedFunctions = tvfDefinitions
                };
                var contextResult = templateContext(contextViewModel);
                var contextResultFileName = Path.Combine(modelsDirectory, contextViewModel.ContextName + ".cs");
                File.WriteAllText(contextResultFileName, contextResult);

                fileNames.Add(contextResultFileName);

                if (_options.CleanUp)
                {
                    var directoryFiles = Directory.GetFiles(modelsDirectory, "*.cs");

                    var filesToCleanUp = directoryFiles.Except(fileNames);

                    foreach (var s in filesToCleanUp)
                    {
                        File.Delete(s);
                    }
                }
            }
        }

        private List<StoredObjectDefinition> GetStoredObjectsDefinition(SqlConnection connection, string sql, bool isFunction)
        {
            var storedProcedureParameters = connection.ReadObjects<StoredObjectParameter>(sql);
            var spDefinitions = (from p in storedProcedureParameters
                group p by new { p.Schema, p.Name }
                into sGroup
                select new StoredObjectDefinition
                {
                    Schema = sGroup.Key.Schema,
                    Name = sGroup.Key.Name,
                    IsFunction = isFunction,
                    Parameters = sGroup.Where(p => !string.IsNullOrEmpty(p.ParameterName)).Select(p => new StoredObjectParameter
                    {
                        ParameterName = p.ParameterName,
                        Schema = sGroup.Key.Schema,
                        Name = sGroup.Key.Name,
                        Order = p.Order,
                        IsOutput = p.IsOutput,
                        IsNullable = p.IsNullable,
                        SqlType = p.SqlType
                    }).OrderBy(p => p.Order).ToList()
                }).ToList();
            return spDefinitions;
        }
    }
}