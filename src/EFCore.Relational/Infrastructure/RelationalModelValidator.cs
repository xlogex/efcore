﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace Microsoft.EntityFrameworkCore.Infrastructure;

/// <summary>
///     The validator that enforces rules common for all relational providers.
/// </summary>
/// <remarks>
///     <para>
///         The service lifetime is <see cref="ServiceLifetime.Singleton" />. This means a single instance
///         is used by many <see cref="DbContext" /> instances. The implementation must be thread-safe.
///         This service cannot depend on services registered as <see cref="ServiceLifetime.Scoped" />.
///     </para>
///     <para>
///         See <see href="https://aka.ms/efcore-docs-providers">Implementation of database providers and extensions</see>
///         for more information and examples.
///     </para>
/// </remarks>
public class RelationalModelValidator : ModelValidator
{
    /// <summary>
    ///     Creates a new instance of <see cref="RelationalModelValidator" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this service.</param>
    /// <param name="relationalDependencies">Parameter object containing relational dependencies for this service.</param>
    public RelationalModelValidator(
        ModelValidatorDependencies dependencies,
        RelationalModelValidatorDependencies relationalDependencies)
        : base(dependencies)
    {
        RelationalDependencies = relationalDependencies;
    }

    /// <summary>
    ///     Relational provider-specific dependencies for this service.
    /// </summary>
    protected virtual RelationalModelValidatorDependencies RelationalDependencies { get; }

    /// <summary>
    ///     Validates a model, throwing an exception if any errors are found.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    public override void Validate(IModel model, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        base.Validate(model, logger);

        ValidateMappingFragments(model, logger);
        ValidatePropertyOverrides(model, logger);
        ValidateSqlQueries(model, logger);
        ValidateDbFunctions(model, logger);
        ValidateSharedTableCompatibility(model, logger);
        ValidateSharedViewCompatibility(model, logger);
        ValidateDefaultValuesOnKeys(model, logger);
        ValidateBoolsWithDefaults(model, logger);
        ValidateIndexProperties(model, logger);
        ValidateTriggers(model, logger);
    }

    /// <summary>
    ///     Validates the mapping/configuration of SQL queries in the model.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSqlQueries(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var sqlQuery = entityType.GetSqlQuery();
            if (sqlQuery == null)
            {
                continue;
            }

            if (entityType.BaseType != null
                && (entityType.FindDiscriminatorProperty() == null
                    || sqlQuery != entityType.BaseType.GetSqlQuery()))
            {
                throw new InvalidOperationException(
                    RelationalStrings.InvalidMappedSqlQueryDerivedType(
                        entityType.DisplayName(), entityType.BaseType.DisplayName()));
            }
        }
    }

    /// <summary>
    ///     Validates the mapping/configuration of functions in the model.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateDbFunctions(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var dbFunction in model.GetDbFunctions())
        {
            if (dbFunction.IsScalar)
            {
                if (dbFunction.TypeMapping == null)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.DbFunctionInvalidReturnType(
                            dbFunction.ModelName,
                            dbFunction.ReturnType.ShortDisplayName()));
                }
            }
            else
            {
                var elementType = dbFunction.ReturnType.GetGenericArguments()[0];
                var entityType = model.FindEntityType(elementType);

                if (entityType?.IsOwned() == true
                    || ((IConventionModel)model).IsOwned(elementType)
                    || (entityType == null && model.GetEntityTypes().Any(e => e.ClrType == elementType)))
                {
                    throw new InvalidOperationException(
                        RelationalStrings.DbFunctionInvalidIQueryableOwnedReturnType(
                            dbFunction.ModelName, elementType.ShortDisplayName()));
                }

                if (entityType == null)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.DbFunctionInvalidReturnEntityType(
                            dbFunction.ModelName, dbFunction.ReturnType.ShortDisplayName(), elementType.ShortDisplayName()));
                }

                if ((entityType.BaseType != null || entityType.GetDerivedTypes().Any())
                    && entityType.FindDiscriminatorProperty() == null)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.TableValuedFunctionNonTph(dbFunction.ModelName, entityType.DisplayName()));
                }
            }

            foreach (var parameter in dbFunction.Parameters)
            {
                if (parameter.TypeMapping == null)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.DbFunctionInvalidParameterType(
                            parameter.Name,
                            dbFunction.ModelName,
                            parameter.ClrType.ShortDisplayName()));
                }
            }
        }

        foreach (var entityType in model.GetEntityTypes())
        {
            var mappedFunctionName = entityType.GetFunctionName();
            if (mappedFunctionName == null)
            {
                continue;
            }

            var mappedFunction = model.FindDbFunction(mappedFunctionName);
            if (mappedFunction == null)
            {
                throw new InvalidOperationException(
                    RelationalStrings.MappedFunctionNotFound(entityType.DisplayName(), mappedFunctionName));
            }

            if (entityType.BaseType != null)
            {
                throw new InvalidOperationException(
                    RelationalStrings.InvalidMappedFunctionDerivedType(
                        entityType.DisplayName(), mappedFunctionName, entityType.BaseType.DisplayName()));
            }

            if (mappedFunction.IsScalar
                || mappedFunction.ReturnType.GetGenericArguments()[0] != entityType.ClrType)
            {
                throw new InvalidOperationException(
                    RelationalStrings.InvalidMappedFunctionUnmatchedReturn(
                        entityType.DisplayName(),
                        mappedFunctionName,
                        mappedFunction.ReturnType.ShortDisplayName(),
                        entityType.ClrType.ShortDisplayName()));
            }

            if (mappedFunction.Parameters.Count > 0)
            {
                var parameters = "{"
                    + string.Join(
                        ", ",
                        mappedFunction.Parameters.Select(p => "'" + p.Name + "'"))
                    + "}";
                throw new InvalidOperationException(
                    RelationalStrings.InvalidMappedFunctionWithParameters(
                        entityType.DisplayName(), mappedFunctionName, parameters));
            }
        }
    }

    /// <summary>
    ///     Validates the mapping/configuration of <see cref="bool" /> properties in the model.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateBoolsWithDefaults(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.ClrType != typeof(bool)
                    || property.ValueGenerated == ValueGenerated.Never)
                {
                    continue;
                }

                if (StoreObjectIdentifier.Create(property.DeclaringEntityType, StoreObjectType.Table) is StoreObjectIdentifier table
                    && (IsNotNullAndFalse(property.GetDefaultValue(table))
                        || property.GetDefaultValueSql(table) != null))
                {
                    logger.BoolWithDefaultWarning(property);
                }
            }
        }

        static bool IsNotNullAndFalse(object? value)
            => value != null
                && (!(value is bool asBool) || asBool);
    }

    /// <summary>
    ///     Validates the mapping/configuration of default values in the model.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateDefaultValuesOnKeys(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var key in entityType.GetDeclaredKeys())
            {
                foreach (var property in key.Properties)
                {
                    var defaultValue = (IConventionAnnotation?)property.FindAnnotation(RelationalAnnotationNames.DefaultValue);
                    if (defaultValue?.Value != null
                        && defaultValue.GetConfigurationSource().Overrides(ConfigurationSource.DataAnnotation))
                    {
                        logger.ModelValidationKeyDefaultValueWarning(property);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Validates the mapping/configuration of shared tables in the model.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedTableCompatibility(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var tables = new Dictionary<StoreObjectIdentifier, List<IEntityType>>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName == null)
            {
                continue;
            }

            var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            if (!tables.TryGetValue(table, out var mappedTypes))
            {
                mappedTypes = new List<IEntityType>();
                tables[table] = mappedTypes;
            }

            mappedTypes.Add(entityType);
        }

        foreach (var (table, mappedTypes) in tables)
        {
            ValidateSharedTableCompatibility(mappedTypes, table.Name, table.Schema, logger);
            ValidateSharedColumnsCompatibility(mappedTypes, table, logger);
            ValidateSharedKeysCompatibility(mappedTypes, table, logger);
            ValidateSharedForeignKeysCompatibility(mappedTypes, table, logger);
            ValidateSharedIndexesCompatibility(mappedTypes, table, logger);
            ValidateSharedCheckConstraintCompatibility(mappedTypes, table, logger);
            ValidateSharedTriggerCompatibility(mappedTypes, table, logger);

            // Validate optional dependents
            if (mappedTypes.Count == 1)
            {
                continue;
            }

            var principalEntityTypesMap = new Dictionary<IEntityType, (List<IEntityType> EntityTypes, bool Optional)>();
            foreach (var entityType in mappedTypes)
            {
                if (entityType.BaseType != null
                    || entityType.FindPrimaryKey() == null)
                {
                    continue;
                }

                var (principalEntityTypes, optional) = GetPrincipalEntityTypes(entityType);
                if (!optional)
                {
                    continue;
                }

                var principalColumns = principalEntityTypes.SelectMany(e => e.GetProperties())
                    .Select(e => e.GetColumnName(table))
                    .Where(e => e != null)
                    .ToList();
                var requiredNonSharedColumnFound = false;
                foreach (var property in entityType.GetProperties())
                {
                    if (property.IsPrimaryKey()
                        || property.IsNullable)
                    {
                        continue;
                    }

                    var columnName = property.GetColumnName(table);
                    if (columnName != null)
                    {
                        if (!principalColumns.Contains(columnName))
                        {
                            requiredNonSharedColumnFound = true;
                            break;
                        }
                    }
                }

                if (!requiredNonSharedColumnFound)
                {
                    if (entityType.GetReferencingForeignKeys().Select(e => e.DeclaringEntityType).Any(t => mappedTypes.Contains(t)))
                    {
                        throw new InvalidOperationException(
                            RelationalStrings.OptionalDependentWithDependentWithoutIdentifyingProperty(entityType.DisplayName()));
                    }

                    logger.OptionalDependentWithoutIdentifyingPropertyWarning(entityType);
                }
            }

            (List<IEntityType> EntityTypes, bool Optional) GetPrincipalEntityTypes(IEntityType entityType)
            {
                if (!principalEntityTypesMap.TryGetValue(entityType, out var tuple))
                {
                    var list = new List<IEntityType>();
                    var optional = false;
                    foreach (var foreignKey in entityType.FindForeignKeys(entityType.FindPrimaryKey()!.Properties))
                    {
                        var principalEntityType = foreignKey.PrincipalEntityType;
                        if (!mappedTypes.Contains(principalEntityType))
                        {
                            continue;
                        }

                        list.Add(principalEntityType);
                        var (entityTypes, innerOptional) = GetPrincipalEntityTypes(principalEntityType.GetRootType());
                        list.AddRange(entityTypes);

                        optional |= !foreignKey.IsRequiredDependent | innerOptional;
                    }

                    tuple = (list, optional);
                    principalEntityTypesMap.Add(entityType, tuple);
                }

                return tuple;
            }
        }
    }

    /// <summary>
    ///     Validates the compatibility of entity types sharing a given table.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedTableCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        string tableName,
        string? schema,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        if (mappedTypes.Count == 1)
        {
            return;
        }

        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        var unvalidatedTypes = new HashSet<IEntityType>(mappedTypes);
        IEntityType? root = null;
        foreach (var mappedType in mappedTypes)
        {
            if (mappedType.BaseType != null && unvalidatedTypes.Contains(mappedType.BaseType))
            {
                continue;
            }

            var primaryKey = mappedType.FindPrimaryKey();
            if (primaryKey != null
                && (mappedType.FindForeignKeys(primaryKey.Properties)
                    .FirstOrDefault(
                        fk => fk.PrincipalKey.IsPrimaryKey()
                            && unvalidatedTypes.Contains(fk.PrincipalEntityType)) is IForeignKey linkingFK))
            {
                if (mappedType.BaseType != null)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.IncompatibleTableDerivedRelationship(
                            storeObject.DisplayName(),
                            mappedType.DisplayName(),
                            linkingFK.PrincipalEntityType.DisplayName()));
                }

                continue;
            }

            if (root != null)
            {
                throw new InvalidOperationException(
                    RelationalStrings.IncompatibleTableNoRelationship(
                        storeObject.DisplayName(),
                        mappedType.DisplayName(),
                        root.DisplayName()));
            }

            root = mappedType;
        }

        Check.DebugAssert(root != null, "root != null");
        unvalidatedTypes.Remove(root);
        var typesToValidate = new Queue<IEntityType>();
        typesToValidate.Enqueue(root);

        while (typesToValidate.Count > 0)
        {
            var entityType = typesToValidate.Dequeue();
            var key = entityType.FindPrimaryKey();
            var comment = entityType.GetComment();
            var isExcluded = entityType.IsTableExcludedFromMigrations(storeObject);
            var typesToValidateLeft = typesToValidate.Count;
            var directlyConnectedTypes = unvalidatedTypes.Where(
                unvalidatedType =>
                    entityType.IsAssignableFrom(unvalidatedType)
                    || IsIdentifyingPrincipal(unvalidatedType, entityType));

            foreach (var nextEntityType in directlyConnectedTypes)
            {
                if (key != null)
                {
                    var otherKey = nextEntityType.FindPrimaryKey()!;
                    if (key.GetName(storeObject) != otherKey.GetName(storeObject))
                    {
                        throw new InvalidOperationException(
                            RelationalStrings.IncompatibleTableKeyNameMismatch(
                                storeObject.DisplayName(),
                                entityType.DisplayName(),
                                nextEntityType.DisplayName(),
                                key.GetName(storeObject),
                                key.Properties.Format(),
                                otherKey.GetName(storeObject),
                                otherKey.Properties.Format()));
                    }
                }

                var nextComment = nextEntityType.GetComment();
                if (comment != null)
                {
                    if (nextComment != null
                        && !comment.Equals(nextComment, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            RelationalStrings.IncompatibleTableCommentMismatch(
                                storeObject.DisplayName(),
                                entityType.DisplayName(),
                                nextEntityType.DisplayName(),
                                comment,
                                nextComment));
                    }
                }
                else
                {
                    comment = nextComment;
                }

                if (isExcluded.Equals(!nextEntityType.IsTableExcludedFromMigrations(storeObject)))
                {
                    throw new InvalidOperationException(
                        RelationalStrings.IncompatibleTableExcludedMismatch(
                            storeObject.DisplayName(),
                            entityType.DisplayName(),
                            nextEntityType.DisplayName()));
                }

                typesToValidate.Enqueue(nextEntityType);
            }

            foreach (var typeToValidate in typesToValidate.Skip(typesToValidateLeft))
            {
                unvalidatedTypes.Remove(typeToValidate);
            }
        }

        if (unvalidatedTypes.Count == 0)
        {
            return;
        }

        foreach (var invalidEntityType in unvalidatedTypes)
        {
            Check.DebugAssert(root != null, "root is null");
            throw new InvalidOperationException(
                RelationalStrings.IncompatibleTableNoRelationship(
                    tableName,
                    invalidEntityType.DisplayName(),
                    root.DisplayName()));
        }
    }

    /// <summary>
    ///     Validates the mapping/configuration of shared views in the model.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedViewCompatibility(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var views = new Dictionary<StoreObjectIdentifier, List<IEntityType>>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var viewsName = entityType.GetViewName();
            if (viewsName == null)
            {
                continue;
            }

            var view = StoreObjectIdentifier.View(viewsName, entityType.GetViewSchema());
            if (!views.TryGetValue(view, out var mappedTypes))
            {
                mappedTypes = new List<IEntityType>();
                views[view] = mappedTypes;
            }

            mappedTypes.Add(entityType);
        }

        foreach (var (view, mappedTypes) in views)
        {
            ValidateSharedViewCompatibility(mappedTypes, view.Name, view.Schema, logger);
            ValidateSharedColumnsCompatibility(mappedTypes, view, logger);
        }
    }

    /// <summary>
    ///     Validates the compatibility of entity types sharing a given view.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="viewName">The view name.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedViewCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        string viewName,
        string? schema,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        if (mappedTypes.Count == 1)
        {
            return;
        }

        var storeObject = StoreObjectIdentifier.View(viewName, schema);
        var unvalidatedTypes = new HashSet<IEntityType>(mappedTypes);
        IEntityType? root = null;
        foreach (var mappedType in mappedTypes)
        {
            if (mappedType.BaseType != null && unvalidatedTypes.Contains(mappedType.BaseType))
            {
                continue;
            }

            if (mappedType.FindPrimaryKey() != null
                && mappedType.FindForeignKeys(mappedType.FindPrimaryKey()!.Properties)
                    .Any(
                        fk => fk.PrincipalKey.IsPrimaryKey()
                            && unvalidatedTypes.Contains(fk.PrincipalEntityType)))
            {
                if (mappedType.BaseType != null)
                {
                    var principalType = mappedType.FindForeignKeys(mappedType.FindPrimaryKey()!.Properties)
                        .First(
                            fk => fk.PrincipalKey.IsPrimaryKey()
                                && unvalidatedTypes.Contains(fk.PrincipalEntityType))
                        .PrincipalEntityType;
                    throw new InvalidOperationException(
                        RelationalStrings.IncompatibleViewDerivedRelationship(
                            storeObject.DisplayName(),
                            mappedType.DisplayName(),
                            principalType.DisplayName()));
                }

                continue;
            }

            if (root != null)
            {
                throw new InvalidOperationException(
                    RelationalStrings.IncompatibleViewNoRelationship(
                        storeObject.DisplayName(),
                        mappedType.DisplayName(),
                        root.DisplayName()));
            }

            root = mappedType;
        }

        Check.DebugAssert(root != null, "root != null");
        unvalidatedTypes.Remove(root);
        var typesToValidate = new Queue<IEntityType>();
        typesToValidate.Enqueue(root);

        while (typesToValidate.Count > 0)
        {
            var entityType = typesToValidate.Dequeue();
            var typesToValidateLeft = typesToValidate.Count;
            var directlyConnectedTypes = unvalidatedTypes.Where(
                unvalidatedType =>
                    entityType.IsAssignableFrom(unvalidatedType)
                    || IsIdentifyingPrincipal(unvalidatedType, entityType));

            foreach (var nextEntityType in directlyConnectedTypes)
            {
                typesToValidate.Enqueue(nextEntityType);
            }

            foreach (var typeToValidate in typesToValidate.Skip(typesToValidateLeft))
            {
                unvalidatedTypes.Remove(typeToValidate);
            }
        }

        if (unvalidatedTypes.Count == 0)
        {
            return;
        }

        foreach (var invalidEntityType in unvalidatedTypes)
        {
            Check.DebugAssert(root != null, "root is null");
            throw new InvalidOperationException(
                RelationalStrings.IncompatibleViewNoRelationship(
                    viewName,
                    invalidEntityType.DisplayName(),
                    root.DisplayName()));
        }
    }

    private static bool IsIdentifyingPrincipal(IEntityType dependentEntityType, IEntityType principalEntityType)
        => dependentEntityType.FindForeignKeys(dependentEntityType.FindPrimaryKey()!.Properties)
            .Any(
                fk => fk.PrincipalKey.IsPrimaryKey()
                    && fk.PrincipalEntityType == principalEntityType);

    /// <summary>
    ///     Validates the compatibility of properties sharing columns in a given table-like object.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedColumnsCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var concurrencyColumns = TableSharingConcurrencyTokenConvention.GetConcurrencyTokensMap(storeObject, mappedTypes);
        HashSet<string>? missingConcurrencyTokens = null;
        if (concurrencyColumns != null
            && storeObject.StoreObjectType == StoreObjectType.Table)
        {
            missingConcurrencyTokens = new HashSet<string>();
        }

        var propertyMappings = new Dictionary<string, IProperty>();
        foreach (var entityType in mappedTypes)
        {
            if (missingConcurrencyTokens != null)
            {
                missingConcurrencyTokens.Clear();
                foreach (var (key, readOnlyProperties) in concurrencyColumns!)
                {
                    if (TableSharingConcurrencyTokenConvention.IsConcurrencyTokenMissing(readOnlyProperties, entityType, mappedTypes))
                    {
                        missingConcurrencyTokens.Add(key);
                    }
                }
            }

            foreach (var property in entityType.GetDeclaredProperties())
            {
                var columnName = property.GetColumnName(storeObject);
                if (columnName == null)
                {
                    continue;
                }
                
                missingConcurrencyTokens?.Remove(columnName);
                if (!propertyMappings.TryGetValue(columnName, out var duplicateProperty))
                {
                    propertyMappings[columnName] = property;
                    continue;
                }

                ValidateCompatible(property, duplicateProperty, columnName, storeObject, logger);
            }

            if (missingConcurrencyTokens != null)
            {
                foreach (var missingColumn in missingConcurrencyTokens)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.MissingConcurrencyColumn(
                            entityType.DisplayName(), missingColumn, storeObject.DisplayName()));
                }
            }
        }

        var columnOrders = new Dictionary<int, List<string>>();
        foreach (var property in propertyMappings.Values)
        {
            var columnOrder = property.GetColumnOrder(storeObject);
            if (!columnOrder.HasValue)
            {
                continue;
            }

            var columns = columnOrders.GetOrAddNew(columnOrder.Value);
            columns.Add(property.GetColumnName(storeObject)!);
        }

        if (columnOrders.Any(g => g.Value.Count > 1))
        {
            logger.DuplicateColumnOrders(
                storeObject,
                columnOrders.Where(g => g.Value.Count > 1).SelectMany(g => g.Value).ToList());
        }
    }

    /// <summary>
    ///     Validates the compatibility of two properties mapped to the same column.
    /// </summary>
    /// <param name="property">A property.</param>
    /// <param name="duplicateProperty">Another property.</param>
    /// <param name="columnName">The column name.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateCompatible(
        IProperty property,
        IProperty duplicateProperty,
        string columnName,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        if (property.IsColumnNullable(storeObject) != duplicateProperty.IsColumnNullable(storeObject))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameNullabilityMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName()));
        }

        var currentMaxLength = property.GetMaxLength(storeObject);
        var previousMaxLength = duplicateProperty.GetMaxLength(storeObject);
        if (currentMaxLength != previousMaxLength)
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameMaxLengthMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousMaxLength,
                    currentMaxLength));
        }

        if (property.IsUnicode(storeObject) != duplicateProperty.IsUnicode(storeObject))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameUnicodenessMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName()));
        }

        if (property.IsFixedLength(storeObject) != duplicateProperty.IsFixedLength(storeObject))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameFixedLengthMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName()));
        }

        var currentPrecision = property.GetPrecision(storeObject);
        var previousPrecision = duplicateProperty.GetPrecision(storeObject);
        if (currentPrecision != previousPrecision)
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNamePrecisionMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    currentPrecision,
                    previousPrecision));
        }

        var currentScale = property.GetScale(storeObject);
        var previousScale = duplicateProperty.GetScale(storeObject);
        if (currentScale != previousScale)
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameScaleMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    currentScale,
                    previousScale));
        }

        if (property.IsConcurrencyToken != duplicateProperty.IsConcurrencyToken)
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameConcurrencyTokenMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName()));
        }

        var typeMapping = property.GetRelationalTypeMapping();
        var duplicateTypeMapping = duplicateProperty.GetRelationalTypeMapping();
        var currentTypeString = property.GetColumnType(storeObject)
            ?? typeMapping.StoreType;
        var previousTypeString = duplicateProperty.GetColumnType(storeObject)
            ?? duplicateTypeMapping.StoreType;
        if (!string.Equals(currentTypeString, previousTypeString, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameDataTypeMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousTypeString,
                    currentTypeString));
        }

        var currentProviderType = typeMapping.Converter?.ProviderClrType ?? typeMapping.ClrType;
        var previousProviderType = duplicateTypeMapping.Converter?.ProviderClrType ?? duplicateTypeMapping.ClrType;
        if (currentProviderType != previousProviderType)
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameProviderTypeMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousProviderType.ShortDisplayName(),
                    currentProviderType.ShortDisplayName()));
        }

        var currentComputedColumnSql = property.GetComputedColumnSql(storeObject) ?? "";
        var previousComputedColumnSql = duplicateProperty.GetComputedColumnSql(storeObject) ?? "";
        if (!currentComputedColumnSql.Equals(previousComputedColumnSql, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameComputedSqlMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousComputedColumnSql,
                    currentComputedColumnSql));
        }

        var currentStored = property.GetIsStored(storeObject);
        var previousStored = duplicateProperty.GetIsStored(storeObject);
        if (currentStored != previousStored)
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameIsStoredMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousStored,
                    currentStored));
        }

        var hasDefaultValue = property.TryGetDefaultValue(storeObject, out var currentDefaultValue);
        var duplicateHasDefaultValue = duplicateProperty.TryGetDefaultValue(storeObject, out var previousDefaultValue);
        if ((hasDefaultValue
                || duplicateHasDefaultValue)
            && !Equals(currentDefaultValue, previousDefaultValue))
        {
            currentDefaultValue = GetDefaultColumnValue(property, storeObject);
            previousDefaultValue = GetDefaultColumnValue(duplicateProperty, storeObject);

            if (!Equals(currentDefaultValue, previousDefaultValue))
            {
                throw new InvalidOperationException(
                    RelationalStrings.DuplicateColumnNameDefaultSqlMismatch(
                        duplicateProperty.DeclaringEntityType.DisplayName(),
                        duplicateProperty.Name,
                        property.DeclaringEntityType.DisplayName(),
                        property.Name,
                        columnName,
                        storeObject.DisplayName(),
                        previousDefaultValue ?? "NULL",
                        currentDefaultValue ?? "NULL"));
            }
        }

        var currentDefaultValueSql = property.GetDefaultValueSql(storeObject) ?? "";
        var previousDefaultValueSql = duplicateProperty.GetDefaultValueSql(storeObject) ?? "";
        if (!currentDefaultValueSql.Equals(previousDefaultValueSql, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameDefaultSqlMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousDefaultValueSql,
                    currentDefaultValueSql));
        }

        var currentComment = property.GetComment(storeObject) ?? "";
        var previousComment = duplicateProperty.GetComment(storeObject) ?? "";
        if (!currentComment.Equals(previousComment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameCommentMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousComment,
                    currentComment));
        }

        var currentCollation = property.GetCollation(storeObject) ?? "";
        var previousCollation = duplicateProperty.GetCollation(storeObject) ?? "";
        if (!currentCollation.Equals(previousCollation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameCollationMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousCollation,
                    currentCollation));
        }

        var currentColumnOrder = property.GetColumnOrder(storeObject);
        var previousColumnOrder = duplicateProperty.GetColumnOrder(storeObject);
        if (currentColumnOrder != previousColumnOrder)
        {
            throw new InvalidOperationException(
                RelationalStrings.DuplicateColumnNameOrderMismatch(
                    duplicateProperty.DeclaringEntityType.DisplayName(),
                    duplicateProperty.Name,
                    property.DeclaringEntityType.DisplayName(),
                    property.Name,
                    columnName,
                    storeObject.DisplayName(),
                    previousColumnOrder,
                    currentColumnOrder));
        }
    }

    /// <summary>
    ///     Returns the object that is used as the default value for the column the property is mapped to.
    /// </summary>
    /// <param name="property">The property to get the default value for.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <returns>The object that is used as the default value for the column the property is mapped to.</returns>
    protected virtual object? GetDefaultColumnValue(
        IProperty property,
        in StoreObjectIdentifier storeObject)
    {
        var value = property.GetDefaultValue(storeObject);
        var converter = property.FindRelationalTypeMapping(storeObject)?.Converter;

        return converter != null
            ? converter.ConvertToProvider(value)
            : value;
    }

    /// <summary>
    ///     Validates the compatibility of foreign keys in a given shared table.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedForeignKeysCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        if (storeObject.StoreObjectType != StoreObjectType.Table)
        {
            return;
        }

        var foreignKeyMappings = new Dictionary<string, IForeignKey>();

        foreach (var foreignKey in mappedTypes.SelectMany(et => et.GetDeclaredForeignKeys()))
        {
            var principalTable = foreignKey.PrincipalKey.IsPrimaryKey()
                ? StoreObjectIdentifier.Create(foreignKey.PrincipalEntityType, StoreObjectType.Table)
                : StoreObjectIdentifier.Create(foreignKey.PrincipalKey.DeclaringEntityType, StoreObjectType.Table);
            if (principalTable == null)
            {
                continue;
            }

            var foreignKeyName = foreignKey.GetConstraintName(storeObject, principalTable.Value);
            if (foreignKeyName == null)
            {
                if (foreignKey.PrincipalEntityType.GetMappingStrategy() == RelationalAnnotationNames.TpcMappingStrategy)
                {
                    logger.ForeignKeyTpcPrincipalWarning(foreignKey);
                }

                var derivedTables = foreignKey.DeclaringEntityType.GetDerivedTypes()
                    .Select(t => StoreObjectIdentifier.Create(t, StoreObjectType.Table))
                    .Where(t => t != null);
                if (foreignKey.GetConstraintName() != null
                    && derivedTables.All(
                        t => foreignKey.GetConstraintName(
                                t!.Value,
                                principalTable.Value)
                            == null))
                {
                    logger.ForeignKeyPropertiesMappedToUnrelatedTables(foreignKey);
                }

                continue;
            }

            if (!foreignKeyMappings.TryGetValue(foreignKeyName, out var duplicateForeignKey))
            {
                foreignKeyMappings[foreignKeyName] = foreignKey;
                continue;
            }

            ValidateCompatible(foreignKey, duplicateForeignKey, foreignKeyName, storeObject, logger);
        }
    }

    /// <summary>
    ///     Validates the compatibility of two foreign keys mapped to the same foreign key constraint.
    /// </summary>
    /// <param name="foreignKey">A foreign key.</param>
    /// <param name="duplicateForeignKey">Another foreign key.</param>
    /// <param name="foreignKeyName">The foreign key constraint name.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateCompatible(
        IForeignKey foreignKey,
        IForeignKey duplicateForeignKey,
        string foreignKeyName,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
        => foreignKey.AreCompatible(duplicateForeignKey, storeObject, shouldThrow: true);

    /// <summary>
    ///     Validates the compatibility of indexes in a given shared table.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedIndexesCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var indexMappings = new Dictionary<string, IIndex>();
        foreach (var index in mappedTypes.SelectMany(et => et.GetDeclaredIndexes()))
        {
            var indexName = index.GetDatabaseName(storeObject);
            if (indexName == null)
            {
                continue;
            }

            if (!indexMappings.TryGetValue(indexName, out var duplicateIndex))
            {
                indexMappings[indexName] = index;
                continue;
            }

            ValidateCompatible(index, duplicateIndex, indexName, storeObject, logger);
        }
    }

    /// <summary>
    ///     Validates the compatibility of two indexes mapped to the same table index.
    /// </summary>
    /// <param name="index">An index.</param>
    /// <param name="duplicateIndex">Another index.</param>
    /// <param name="indexName">The name of the index.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateCompatible(
        IIndex index,
        IIndex duplicateIndex,
        string indexName,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
        => index.AreCompatible(duplicateIndex, storeObject, shouldThrow: true);

    /// <summary>
    ///     Validates the compatibility of primary and alternate keys in a given shared table.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedKeysCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var keyMappings = new Dictionary<string, IKey>();
        foreach (var key in mappedTypes.SelectMany(et => et.GetDeclaredKeys()))
        {
            var keyName = key.GetName(storeObject);
            if (keyName == null)
            {
                continue;
            }

            if (!keyMappings.TryGetValue(keyName, out var duplicateKey))
            {
                keyMappings[keyName] = key;
                continue;
            }

            ValidateCompatible(key, duplicateKey, keyName, storeObject, logger);
        }
    }

    /// <summary>
    ///     Validates the compatibility of two keys mapped to the same unique constraint.
    /// </summary>
    /// <param name="key">A key.</param>
    /// <param name="duplicateKey">Another key.</param>
    /// <param name="keyName">The name of the unique constraint.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateCompatible(
        IKey key,
        IKey duplicateKey,
        string keyName,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
        => key.AreCompatible(duplicateKey, storeObject, shouldThrow: true);

    /// <summary>
    ///     Validates the compatibility of check constraints in a given shared table.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedCheckConstraintCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var checkConstraintMappings = new Dictionary<string, ICheckConstraint>();
        foreach (var checkConstraint in mappedTypes.SelectMany(et => et.GetDeclaredCheckConstraints()))
        {
            var checkConstraintName = checkConstraint.GetName(storeObject);
            if (checkConstraintName == null)
            {
                continue;
            }

            if (!checkConstraintMappings.TryGetValue(checkConstraintName, out var duplicateCheckConstraint))
            {
                checkConstraintMappings[checkConstraintName] = checkConstraint;
                continue;
            }

            ValidateCompatible(checkConstraint, duplicateCheckConstraint, checkConstraintName, storeObject, logger);
        }
    }

    /// <summary>
    ///     Validates the compatibility of two check constraints with the same name.
    /// </summary>
    /// <param name="checkConstraint">A check constraint.</param>
    /// <param name="duplicateCheckConstraint">Another check constraint.</param>
    /// <param name="indexName">The name of the check constraint.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateCompatible(
        ICheckConstraint checkConstraint,
        ICheckConstraint duplicateCheckConstraint,
        string indexName,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
        => CheckConstraint.AreCompatible(checkConstraint, duplicateCheckConstraint, storeObject, shouldThrow: true);

    /// <summary>
    ///     Validates the compatibility of triggers in a given shared table.
    /// </summary>
    /// <param name="mappedTypes">The mapped entity types.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateSharedTriggerCompatibility(
        IReadOnlyList<IEntityType> mappedTypes,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var triggerMappings = new Dictionary<string, ITrigger>();
        foreach (var trigger in mappedTypes.SelectMany(et => et.GetDeclaredTriggers()))
        {
            var triggerName = trigger.GetName(storeObject);
            if (triggerName == null)
            {
                continue;
            }

            if (!triggerMappings.TryGetValue(triggerName, out var duplicateTrigger))
            {
                triggerMappings[triggerName] = trigger;
                continue;
            }

            ValidateCompatible(trigger, duplicateTrigger, triggerName, storeObject, logger);
        }
    }

    /// <summary>
    ///     Validates the compatibility of two trigger with the same name.
    /// </summary>
    /// <param name="trigger">A trigger.</param>
    /// <param name="duplicateTrigger">Another trigger.</param>
    /// <param name="indexName">The name of the trigger.</param>
    /// <param name="storeObject">The identifier of the store object.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateCompatible(
        ITrigger trigger,
        ITrigger duplicateTrigger,
        string indexName,
        in StoreObjectIdentifier storeObject,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
    }

    /// <summary>
    ///     Validates the mapping/configuration of inheritance in the model.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected override void ValidateInheritanceMapping(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var mappingStrategy = (string?)entityType[RelationalAnnotationNames.MappingStrategy];
            if (mappingStrategy != null)
            {
                ValidateMappingStrategy(entityType, mappingStrategy);
                var storeObject = entityType.GetSchemaQualifiedTableName()
                    ?? entityType.GetSchemaQualifiedViewName()
                    ?? entityType.GetFunctionName();
                if (mappingStrategy == RelationalAnnotationNames.TpcMappingStrategy
                    && !entityType.ClrType.IsInstantiable()
                    && storeObject != null)
                {
                    throw new InvalidOperationException(
                       RelationalStrings.AbstractTpc(entityType.DisplayName(), storeObject));
                }
            }

            if (entityType.BaseType != null)
            {
                if (mappingStrategy != null
                    && mappingStrategy != (string?)entityType.BaseType[RelationalAnnotationNames.MappingStrategy])
                {
                    throw new InvalidOperationException(
                       RelationalStrings.DerivedStrategy(entityType.DisplayName(), mappingStrategy));
                }

                continue;
            }

            if (!entityType.GetDirectlyDerivedTypes().Any())
            {
                continue;
            }

            // Hierarchy mapping strategy must be the same across all types of mappings
            if (entityType.FindDiscriminatorProperty() != null)
            {
                if (mappingStrategy != null
                    && mappingStrategy != RelationalAnnotationNames.TphMappingStrategy)
                {
                    throw new InvalidOperationException(
                       RelationalStrings.NonTphMappingStrategy(mappingStrategy, entityType.DisplayName()));
                }

                ValidateTphMapping(entityType, forTables: false);
                ValidateTphMapping(entityType, forTables: true);
                ValidateDiscriminatorValues(entityType);
            }
            else
            {
                var primaryKey = entityType.FindPrimaryKey();
                if (mappingStrategy == RelationalAnnotationNames.TpcMappingStrategy)
                {
                    var storeGeneratedProperty = primaryKey?.Properties.FirstOrDefault(p => (p.ValueGenerated & ValueGenerated.OnAdd) != 0);
                    if (storeGeneratedProperty != null
                        && entityType.GetTableName() != null)
                    {
                        logger.TpcStoreGeneratedIdentityWarning(storeGeneratedProperty);
                    }

                    foreach (var fk in entityType.GetDeclaredReferencingForeignKeys())
                    {
                        AssertNonInternal(fk, StoreObjectType.View);
                        AssertNonInternal(fk, StoreObjectType.Table);
                    }
                }
                else if (primaryKey == null)
                {
                    throw new InvalidOperationException(
                       RelationalStrings.KeylessMappingStrategy(mappingStrategy ?? RelationalAnnotationNames.TptMappingStrategy, entityType.DisplayName()));
                }

                ValidateNonTphMapping(entityType, forTables: false);
                ValidateNonTphMapping(entityType, forTables: true);

                var derivedTypes = entityType.GetDerivedTypesInclusive().ToList();
                var discriminatorValues = new Dictionary<string, IEntityType>();
                foreach (var derivedType in derivedTypes)
                {
                    if (!derivedType.ClrType.IsInstantiable())
                    {
                        continue;
                    }
                    var discriminatorValue = derivedType.GetDiscriminatorValue();
                    if (discriminatorValue is not string valueString)
                    {
                        throw new InvalidOperationException(
                            RelationalStrings.NonTphDiscriminatorValueNotString(discriminatorValue, derivedType.DisplayName()));
                    }

                    if (discriminatorValues.TryGetValue(valueString, out var duplicateEntityType))
                    {
                        throw new InvalidOperationException(RelationalStrings.EntityShortNameNotUnique(
                            derivedType.Name, discriminatorValue, duplicateEntityType.Name));
                    }

                    discriminatorValues[valueString] = derivedType;
                }
            }
        }

        static void AssertNonInternal(IForeignKey foreignKey, StoreObjectType storeObjectType)
        {
            if (!foreignKey.PrincipalKey.IsPrimaryKey()
                || foreignKey.PrincipalEntityType == foreignKey.DeclaringEntityType
                || !foreignKey.IsUnique
                || foreignKey.DeclaringEntityType.FindPrimaryKey() == null
#pragma warning disable EF1001 // Internal EF Core API usage.
                || !PropertyListComparer.Instance.Equals(foreignKey.Properties, foreignKey.DeclaringEntityType.FindPrimaryKey()!.Properties))
#pragma warning restore EF1001 // Internal EF Core API usage.
            {
                return;
            }

            var storeObjectId = StoreObjectIdentifier.Create(foreignKey.DeclaringEntityType, storeObjectType);
            if (storeObjectId == null
                || storeObjectId != StoreObjectIdentifier.Create(foreignKey.PrincipalEntityType, storeObjectType))
            {
                return;
            }

            throw new InvalidOperationException(RelationalStrings.TpcTableSharing(
                foreignKey.DeclaringEntityType.DisplayName(),
                storeObjectId.Value.DisplayName(),
                foreignKey.PrincipalEntityType.DisplayName()));
        }
    }

    /// <summary>
    ///     Validates that the given mapping strategy is supported
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="mappingStrategy">The mapping strategy.</param>
    protected virtual void ValidateMappingStrategy(IEntityType entityType, string? mappingStrategy)
    {
        switch (mappingStrategy)
        {
            case RelationalAnnotationNames.TphMappingStrategy:
            case RelationalAnnotationNames.TpcMappingStrategy:
            case RelationalAnnotationNames.TptMappingStrategy:
                break;
            default:
                throw new InvalidOperationException(RelationalStrings.InvalidMappingStrategy(
                    mappingStrategy, entityType.DisplayName()));
        };
    }

    private static void ValidateNonTphMapping(IEntityType rootEntityType, bool forTables)
    {
        var derivedTypes = new Dictionary<(string, string?), IEntityType>();
        foreach (var entityType in rootEntityType.GetDerivedTypesInclusive())
        {
            var name = forTables ? entityType.GetTableName() : entityType.GetViewName();
            if (name == null)
            {
                continue;
            }

            var schema = forTables ? entityType.GetSchema() : entityType.GetViewSchema();
            if (derivedTypes.TryGetValue((name, schema), out var otherType))
            {
                throw new InvalidOperationException(
                    forTables
                        ? RelationalStrings.NonTphTableClash(
                            entityType.DisplayName(), otherType.DisplayName(), entityType.GetSchemaQualifiedTableName())
                        : RelationalStrings.NonTphViewClash(
                            entityType.DisplayName(), otherType.DisplayName(), entityType.GetSchemaQualifiedViewName()));
            }

            derivedTypes[(name, schema)] = entityType;
        }

        var storeObject = StoreObjectIdentifier.Create(rootEntityType, forTables ? StoreObjectType.Table : StoreObjectType.View);
        if (storeObject == null)
        {
            return;
        }

        var internalForeignKey = rootEntityType.FindRowInternalForeignKeys(storeObject.Value).FirstOrDefault();
        if (internalForeignKey != null
            && derivedTypes.Count > 1
            && rootEntityType.GetMappingStrategy() == RelationalAnnotationNames.TpcMappingStrategy)
        {
            var derivedTypePair = derivedTypes.First(kv => kv.Value != rootEntityType);
            var (derivedName, derivedSchema) = derivedTypePair.Key;
            throw new InvalidOperationException(RelationalStrings.TpcTableSharingDependent(
                rootEntityType.DisplayName(),
                storeObject.Value.DisplayName(),
                derivedTypePair.Value.DisplayName(),
                derivedSchema == null ? derivedName : $"{derivedSchema}.{derivedName}"));
        }
    }

    private static void ValidateTphMapping(IEntityType rootEntityType, bool forTables)
    {
        string? firstName = null;
        string? firstSchema = null;
        IEntityType? firstType = null;
        foreach (var entityType in rootEntityType.GetDerivedTypesInclusive())
        {
            var name = forTables ? entityType.GetTableName() : entityType.GetViewName();
            if (name == null)
            {
                continue;
            }

            if (firstType == null)
            {
                firstType = entityType;
                firstName = forTables ? firstType.GetTableName() : firstType.GetViewName();
                firstSchema = forTables ? firstType.GetSchema() : firstType.GetViewSchema();
                continue;
            }

            var schema = forTables ? entityType.GetSchema() : entityType.GetViewSchema();
            if (name != firstName || schema != firstSchema)
            {
                throw new InvalidOperationException(
                    forTables
                        ? RelationalStrings.TphTableMismatch(
                            entityType.DisplayName(), entityType.GetSchemaQualifiedTableName(),
                            firstType.DisplayName(), firstType.GetSchemaQualifiedTableName())
                        : RelationalStrings.TphViewMismatch(
                            entityType.DisplayName(), entityType.GetSchemaQualifiedViewName(),
                            firstType.DisplayName(), firstType.GetSchemaQualifiedViewName()));
            }
        }
    }

    /// <summary>
    ///     Validates the entity type mapping fragments.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateMappingFragments(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var fragments = EntityTypeMappingFragment.Get(entityType);
            if (fragments == null)
            {
                continue;
            }

            if (entityType.BaseType != null
                || entityType.GetDirectlyDerivedTypes().Any())
            {
                throw new InvalidOperationException(
                    RelationalStrings.EntitySplittingHierarchy(entityType.DisplayName(), fragments.First().StoreObject.DisplayName()));
            }

            var anyTableFragments = false;
            var anyViewFragments = false;
            foreach (var fragment in fragments)
            {
                var mainStoreObject = StoreObjectIdentifier.Create(entityType, fragment.StoreObject.StoreObjectType);
                if (mainStoreObject == null)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.EntitySplittingUnmappedMainFragment(
                            entityType.DisplayName(), fragment.StoreObject.DisplayName(), fragment.StoreObject.StoreObjectType));
                }

                if (fragment.StoreObject == mainStoreObject)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.EntitySplittingConflictingMainFragment(
                            entityType.DisplayName(), fragment.StoreObject.DisplayName()));
                }

                var propertiesFound = false;
                foreach (var property in entityType.GetProperties())
                {
                    var columnName = property.GetColumnName(fragment.StoreObject);
                    if (columnName == null)
                    {
                        if (property.IsPrimaryKey())
                        {
                            throw new InvalidOperationException(
                                RelationalStrings.EntitySplittingMissingPrimaryKey(
                                    entityType.DisplayName(), fragment.StoreObject.DisplayName()));
                        }

                        continue;
                    }
                    
                    if (!property.IsPrimaryKey())
                    {
                        propertiesFound = true;
                    }
                }

                if (!propertiesFound)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.EntitySplittingMissingProperties(
                            entityType.DisplayName(), fragment.StoreObject.DisplayName()));
                }

                switch (fragment.StoreObject.StoreObjectType)
                {
                    case StoreObjectType.Table:
                        anyTableFragments = true;
                        break;
                    case StoreObjectType.View:
                        anyViewFragments = true;
                        break;
                }
            }

            if (anyTableFragments)
            {
                ValidateMainMapping(entityType, StoreObjectIdentifier.Create(entityType, StoreObjectType.Table));
            }

            if (anyViewFragments)
            {
                ValidateMainMapping(entityType, StoreObjectIdentifier.Create(entityType, StoreObjectType.View));
            }
        }

        static StoreObjectIdentifier? ValidateMainMapping(IEntityType entityType, StoreObjectIdentifier? mainObject)
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.IsPrimaryKey())
                {
                    continue;
                }

                if (mainObject != null)
                {
                    var columnName = property.GetColumnName(mainObject.Value);
                    if (columnName != null)
                    {
                        mainObject = null;
                    }
                }
            }

            if (mainObject != null)
            {
                throw new InvalidOperationException(
                    RelationalStrings.EntitySplittingMissingPropertiesMainFragment(
                        entityType.DisplayName(), mainObject.Value.DisplayName()));
            }

            return mainObject;
        }
    }

    /// <summary>
    ///     Validates the table-specific property overrides.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidatePropertyOverrides(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                var storeObjectOverrides = RelationalPropertyOverrides.Get(property);
                if (storeObjectOverrides == null)
                {
                    continue;
                }

                foreach (var storeObjectOverride in storeObjectOverrides)
                {
                    if (GetAllMappedStoreObjects(property, storeObjectOverride.StoreObject.StoreObjectType)
                        .Any(o => o == storeObjectOverride.StoreObject))
                    {
                        continue;
                    }

                    var storeObject = storeObjectOverride.StoreObject;
                    switch (storeObject.StoreObjectType)
                    {
                        case StoreObjectType.Table:
                            throw new InvalidOperationException(
                                RelationalStrings.TableOverrideMismatch(
                                    entityType.DisplayName() + "." + property.Name,
                                    storeObjectOverride.StoreObject.DisplayName()));
                        case StoreObjectType.View:
                            throw new InvalidOperationException(
                                RelationalStrings.ViewOverrideMismatch(
                                    entityType.DisplayName() + "." + property.Name,
                                    storeObjectOverride.StoreObject.DisplayName()));
                        case StoreObjectType.SqlQuery:
                            throw new InvalidOperationException(
                                RelationalStrings.SqlQueryOverrideMismatch(
                                    entityType.DisplayName() + "." + property.Name,
                                    storeObjectOverride.StoreObject.DisplayName()));
                        case StoreObjectType.Function:
                            throw new InvalidOperationException(
                                RelationalStrings.FunctionOverrideMismatch(
                                    entityType.DisplayName() + "." + property.Name,
                                    storeObjectOverride.StoreObject.DisplayName()));
                        default:
                            throw new NotSupportedException(storeObject.StoreObjectType.ToString());
                    }
                }
            }
        }
    }

    private static IEnumerable<StoreObjectIdentifier> GetAllMappedStoreObjects(
        IReadOnlyProperty property, StoreObjectType storeObjectType)
    {
        var mappingStrategy = property.DeclaringEntityType.GetMappingStrategy();
        if (property.IsPrimaryKey())
        {
            var declaringStoreObject = StoreObjectIdentifier.Create(property.DeclaringEntityType, storeObjectType);
            if (declaringStoreObject != null)
            {
                yield return declaringStoreObject.Value;
            }

            if (storeObjectType == StoreObjectType.Function
                || storeObjectType == StoreObjectType.SqlQuery)
            {
                yield break;
            }

            foreach (var fragment in property.DeclaringEntityType.GetMappingFragments())
            {
                yield return fragment.StoreObject;
            }

            foreach (var containingType in property.DeclaringEntityType.GetDerivedTypes())
            {
                var storeObject = StoreObjectIdentifier.Create(containingType, storeObjectType);
                if (storeObject != null)
                {
                    yield return storeObject.Value;

                    if (mappingStrategy == RelationalAnnotationNames.TphMappingStrategy)
                    {
                        yield break;
                    }
                }
            }
        }
        else
        {
            var declaringStoreObject = StoreObjectIdentifier.Create(property.DeclaringEntityType, storeObjectType);
            if (storeObjectType == StoreObjectType.Function
                || storeObjectType == StoreObjectType.SqlQuery)
            {
                if (declaringStoreObject != null)
                {
                    yield return declaringStoreObject.Value;
                }
                yield break;
            }

            if (declaringStoreObject != null)
            {
                if (property.DeclaringEntityType.GetMappingFragments().Any())
                {
                    foreach (var fragment in property.DeclaringEntityType.GetMappingFragments())
                    {
                        var overrides = RelationalPropertyOverrides.Find(property, fragment.StoreObject);
                        if (overrides != null)
                        {
                            yield return fragment.StoreObject;
                            yield break;
                        }
                    }
                }

                yield return declaringStoreObject.Value;
                if (mappingStrategy != RelationalAnnotationNames.TpcMappingStrategy)
                {
                    yield break;
                }
            }

            var tableFound = false;
            var queue = new Queue<IReadOnlyEntityType>();
            queue.Enqueue(property.DeclaringEntityType);
            while (queue.Count > 0 && !tableFound)
            {
                foreach (var containingType in queue.Dequeue().GetDirectlyDerivedTypes())
                {
                    var storeObject = StoreObjectIdentifier.Create(containingType, storeObjectType);
                    if (storeObject != null)
                    {
                        yield return storeObject.Value;
                        tableFound = true;
                        if (mappingStrategy == RelationalAnnotationNames.TphMappingStrategy)
                        {
                            yield break;
                        }
                    }

                    if (!tableFound
                        || mappingStrategy == RelationalAnnotationNames.TpcMappingStrategy)
                    {
                        queue.Enqueue(containingType);
                    }
                    continue;
                }
            }            
        }
    }

    /// <summary>
    ///     Validates that the properties of any one index are all mapped to columns on at least one common table.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateIndexProperties(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var index in entityType.GetDeclaredIndexes()
                         .Where(i => ConfigurationSource.Convention != ((IConventionIndex)i).GetConfigurationSource()))
            {
                IProperty? propertyNotMappedToAnyTable = null;
                Tuple<string, List<(string Table, string? Schema)>>? firstPropertyTables = null;
                Tuple<string, List<(string Table, string? Schema)>>? lastPropertyTables = null;
                HashSet<(string Table, string? Schema)>? overlappingTables = null;
                foreach (var property in index.Properties)
                {
                    var tablesMappedToProperty = property.DeclaringEntityType.GetDerivedTypesInclusive()
                        .Select(t => (t.GetTableName(), t.GetSchema())).Distinct()
                        .Where(n => n.Item1 != null && property.GetColumnName(StoreObjectIdentifier.Table(n.Item1, n.Item2)) != null)!
                        .ToList<(string Table, string? Schema)>();
                    if (tablesMappedToProperty.Count == 0)
                    {
                        propertyNotMappedToAnyTable = property;
                        overlappingTables = null;

                        if (firstPropertyTables != null)
                        {
                            // Property is not mapped but we already found a property that is mapped.
                            break;
                        }

                        continue;
                    }

                    if (firstPropertyTables == null)
                    {
                        firstPropertyTables =
                            new Tuple<string, List<(string Table, string? Schema)>>(property.Name, tablesMappedToProperty);
                    }
                    else
                    {
                        lastPropertyTables =
                            new Tuple<string, List<(string Table, string? Schema)>>(property.Name, tablesMappedToProperty);
                    }

                    if (propertyNotMappedToAnyTable != null)
                    {
                        // Property is mapped but we already found a property that is not mapped.
                        overlappingTables = null;
                        break;
                    }

                    if (overlappingTables == null)
                    {
                        overlappingTables = new HashSet<(string Table, string? Schema)>(tablesMappedToProperty);
                    }
                    else
                    {
                        overlappingTables.IntersectWith(tablesMappedToProperty);
                        if (overlappingTables.Count == 0)
                        {
                            break;
                        }
                    }
                }

                if (overlappingTables == null)
                {
                    if (firstPropertyTables == null)
                    {
                        logger.AllIndexPropertiesNotToMappedToAnyTable(
                            entityType,
                            index);
                    }
                    else
                    {
                        logger.IndexPropertiesBothMappedAndNotMappedToTable(
                            entityType,
                            index,
                            propertyNotMappedToAnyTable!.Name);
                    }
                }
                else if (overlappingTables.Count == 0)
                {
                    Check.DebugAssert(firstPropertyTables != null, nameof(firstPropertyTables));
                    Check.DebugAssert(lastPropertyTables != null, nameof(lastPropertyTables));

                    logger.IndexPropertiesMappedToNonOverlappingTables(
                        entityType,
                        index,
                        firstPropertyTables.Item1,
                        firstPropertyTables.Item2,
                        lastPropertyTables.Item1,
                        lastPropertyTables.Item2);
                }
            }
        }
    }

    /// <summary>
    ///     Validates that the triggers are unambiguously mapped to exactly one table.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    protected virtual void ValidateTriggers(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            var tableSchema = entityType.GetSchema();

            foreach (var trigger in entityType.GetDeclaredTriggers())
            {
                if (tableName is null)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.TriggerOnUnmappedEntityType(trigger.ModelName, entityType.DisplayName()));
                }

                if ((trigger.TableName != tableName)
                    || (trigger.TableSchema is not null && trigger.TableSchema != tableSchema))
                {
                    throw new InvalidOperationException(
                        RelationalStrings.TriggerWithMismatchedTable(
                            trigger.ModelName,
                            (trigger.TableName!, trigger.TableSchema).FormatTable(),
                            entityType.DisplayName(),
                            entityType.GetSchemaQualifiedTableName())
                    );
                }
            }
        }
    }

    /// <summary>
    ///     Throws an <see cref="InvalidOperationException" /> with a message containing provider-specific information, when
    ///     available, indicating possible reasons why the property cannot be mapped.
    /// </summary>
    /// <param name="propertyType">The property CLR type.</param>
    /// <param name="entityType">The entity type.</param>
    /// <param name="unmappedProperty">The property.</param>
    protected override void ThrowPropertyNotMappedException(
        string propertyType,
        IConventionEntityType entityType,
        IConventionProperty unmappedProperty)
    {
        var storeType = unmappedProperty.GetColumnType();
        if (storeType != null)
        {
            throw new InvalidOperationException(
                RelationalStrings.PropertyNotMapped(
                    propertyType,
                    entityType.DisplayName(),
                    unmappedProperty.Name,
                    storeType));
        }

        base.ThrowPropertyNotMappedException(propertyType, entityType, unmappedProperty);
    }
}
