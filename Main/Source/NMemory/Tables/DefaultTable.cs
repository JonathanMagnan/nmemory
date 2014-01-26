﻿// ----------------------------------------------------------------------------------
// <copyright file="DefaultTable.cs" company="NMemory Team">
//     Copyright (C) 2012-2013 NMemory Team
//
//     Permission is hereby granted, free of charge, to any person obtaining a copy
//     of this software and associated documentation files (the "Software"), to deal
//     in the Software without restriction, including without limitation the rights
//     to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//     copies of the Software, and to permit persons to whom the Software is
//     furnished to do so, subject to the following conditions:
//
//     The above copyright notice and this permission notice shall be included in
//     all copies or substantial portions of the Software.
//
//     THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//     IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//     FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//     AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//     LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//     OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//     THE SOFTWARE.
// </copyright>
// ----------------------------------------------------------------------------------

namespace NMemory.Tables
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using NMemory.Common;
    using NMemory.Exceptions;
    using NMemory.Execution;
    using NMemory.Indexes;
    using NMemory.Modularity;
    using NMemory.Transactions;
    using NMemory.Transactions.Logs;

    /// <summary>
    ///     Represents a database table.
    /// </summary>
    /// <typeparam name="TEntity">
    ///     The type of the entities contained by the table
    /// </typeparam>
    /// <typeparam name="TPrimaryKey">
    ///     The type of the primary key of the entities.
    /// </typeparam>
    public class DefaultTable<TEntity, TPrimaryKey> : 
        Table<TEntity, TPrimaryKey> 
        where TEntity : class
    {
        private EntityPropertyCloner<TEntity> cloner;
        private EntityPropertyChangeDetector<TEntity> changeDetector;

        public DefaultTable(
            IDatabase database,
            IKeyInfo<TEntity, TPrimaryKey> primaryKey,
            IdentitySpecification<TEntity> identitySpecification)

            : base(database, primaryKey, identitySpecification)
        {
            this.changeDetector = EntityPropertyChangeDetector<TEntity>.Instance;
            this.cloner = EntityPropertyCloner<TEntity>.Instance;
        }

        /// <summary>
        ///     Prevents a default instance of the <see cref="DefaultTable{TPrimaryKey}" /> 
        ///     class from being created.
        /// </summary>
        private DefaultTable()
            : base(null, null, null)
        {

        }

        #region Insert

        /// <summary>
        ///     Core implementation of an entity insert.
        /// </summary>
        /// <param name="entity">
        ///     The entity that contains the primary key of the entity to be deleted.
        /// </param>
        /// <param name="transaction">
        ///     The transaction within which the update operation executes.
        /// </param>
        protected override void InsertCore(TEntity entity, Transaction transaction)
        {
            IExecutionContext executionContext = 
                new ExecutionContext(this.Database, transaction, OperationType.Insert);

            TEntity storedEntity = this.CreateStoredEntity();
            cloner.Clone(entity, storedEntity);

            this.Executor.ExecuteInsert(storedEntity, executionContext);

            cloner.Clone(storedEntity, entity);
        }

        #endregion

        #region Update

        /// <summary>
        ///     Core implementation of an entity update.
        /// </summary>
        /// <param name="key">
        ///     The primary key of the entity to be updated.
        /// </param>
        /// <param name="entity">
        ///     An entity that contains the new propery values.
        /// </param>
        /// <param name="transaction">
        ///     The transaction within which the update operation is executed.
        /// </param>
        protected override void UpdateCore(TPrimaryKey key, TEntity entity, Transaction transaction)
        {
            this.AcquireWriteLock(transaction);

            try
            {
                TEntity storedEntity = this.PrimaryKeyIndex.GetByUniqueIndex(key);
                Expression<Func<TEntity, TEntity>> updater = CreateSingleEntityUpdater(entity, storedEntity);

                this.UpdateCore(new TEntity[] { storedEntity }, updater, transaction);

                // Copy back the modifications
                this.cloner.Clone(storedEntity, entity);
            }
            finally
            {
                this.ReleaseWriteLock(transaction);
            }
        }

        /// <summary>
        ///     Core implementation of a bulk entity update.
        /// </summary>
        /// <param name="expression">
        ///     A query expression that represents the entities to be updated.
        /// </param>
        /// <param name="updater">
        ///     An expression that represents the update mechanism.
        /// </param>
        /// <param name="transaction">
        ///     The transaction within which the update operation is executed.
        /// </param>
        /// <returns>
        ///     The updated entities.
        /// </returns>
        protected override IEnumerable<TEntity> UpdateCore(
            Expression expression, 
            Expression<Func<TEntity, TEntity>> updater, 
            Transaction transaction)
        {
            // Optimize and compile the query
            List<TEntity> result = null;

            this.AcquireWriteLock(transaction);

            try
            {
                result = this.QueryEntities(expression, transaction);

                this.UpdateCore(result, updater, transaction);
            }
            finally
            {
                this.ReleaseWriteLock(transaction);
            }

            return this.CloneEntities(result);
        }

        private void UpdateCore(
            IList<TEntity> storedEntities, 
            Expression<Func<TEntity, TEntity>> updater, 
            Transaction transaction)
        {
            IExecutionContext executionContext =
                new ExecutionContext(this.Database, transaction, OperationType.Update);

            Func<TEntity, TEntity> updaterFunc = updater.Compile();
            IList<TEntity> updated = new List<TEntity>(storedEntities.Count);

            // Determine which properties of the entities that are about to be updated
            PropertyInfo[] changes = FindPossibleChanges(updater);

            // Determine which indexes are affected by the change
            // If the key of an index containes a changed property, it is affected
            IList<IIndex<TEntity>> affectedIndexes = FindAffectedIndexes(changes);

            // Find relations
            // Add both referring and referred relations!
            RelationGroup relations = this.FindRelations(affectedIndexes);

            // Lock related tables (based on found relations)
            this.LockRelatedTables(transaction, relations);

            // Find the entities referring the entities that are about to be updated
            var referringEntities = 
                this.FindReferringEntities(storedEntities, relations.Referring);

            using (AtomicLogScope logScope = this.StartAtomicLogOperation(transaction))
            {
                // Delete invalid index records (keys are invalid)
                for (int i = 0; i < storedEntities.Count; i++)
                {
                    TEntity storedEntity = storedEntities[i];

                    foreach (IIndex<TEntity> index in affectedIndexes)
                    {
                        index.Delete(storedEntity);
                        logScope.Log.WriteIndexDelete(index, storedEntity);
                    }
                }

                // Modify entity properties
                for (int i = 0; i < storedEntities.Count; i++)
                {
                    TEntity storedEntity = storedEntities[i];

                    // Create backup
                    TEntity backup = Activator.CreateInstance<TEntity>();
                    this.cloner.Clone(storedEntity, backup);
                    TEntity newEntity = updaterFunc.Invoke(storedEntity);

                    // Apply contraints on the entity
                    this.Contraints.Apply(newEntity, executionContext);

                    // Update entity
                    this.cloner.Clone(newEntity, storedEntity);
                    logScope.Log.WriteEntityUpdate(this.cloner, storedEntity, backup);
                }

                // Insert to indexes the entities were removed from
                for (int i = 0; i < storedEntities.Count; i++)
                {
                    TEntity storedEntity = storedEntities[i];

                    foreach (IIndex<TEntity> index in affectedIndexes)
                    {
                        index.Insert(storedEntity);
                        logScope.Log.WriteIndexInsert(index, storedEntity);
                    }
                }

                // Validate the updated entities
                this.ValidateForeignKeys(relations.Referred, storedEntities);

                // Validate the entities that were referring to the old version of entities
                this.ValidateForeignKeys(relations.Referring, referringEntities);

                logScope.Complete();
            }
        }

        #endregion

        #region Delete

        /// <summary>
        ///     Core implementation of an entity delete.
        /// </summary>
        /// <param name="key">
        ///     The primary key of the entity to be deleted.
        /// </param>
        /// <param name="transaction">
        ///     The transaction within which the delete operation is executed.
        /// </param>
        protected override int DeleteCore(
            Expression expression, 
            Transaction transaction)
        {
            IExecutionContext context = 
                new ExecutionContext(
                    this.Database, 
                    transaction,
                    OperationType.Delete);

            var query = this.Compiler.Compile<IEnumerable<TEntity>>(expression);

            return this.Executor.ExecuteDelete(query, context).Count();
        }

        #endregion

        protected ICommandExecutor Executor
        {
            get { return this.Database.DatabaseEngine.Executor; }
        }

        protected IQueryCompiler Compiler
        {
            get { return this.Database.DatabaseEngine.Compiler; }
        }

        private List<TEntity> QueryEntities(Expression expression, Transaction transaction)
        {
            IExecutionPlan<IEnumerable<TEntity>> plan = this.Database.DatabaseEngine.Compiler.Compile<IEnumerable<TEntity>>(expression);
            
            // Find the remaining tables of the query
            ITable[] tables = TableLocator.FindAffectedTables(this.Database, plan).Except(new ITable[] { this }).ToArray();
            
            IExecutionContext context = 
                new ExecutionContext(
                    this.Database, 
                    transaction,
                    OperationType.Query);

            // Lock these tables
            for (int i = 0; i < tables.Length; i++)
            {
                this.Database.DatabaseEngine.ConcurrencyManager.AcquireTableReadLock(tables[i], transaction);
            }

            try
            {
                return plan.Execute(context).Distinct().ToList();
            }
            finally
            {
                // Release the tables locks
                for (int i = 0; i < tables.Length; i++)
                {
                    this.Database.DatabaseEngine.ConcurrencyManager.AcquireTableReadLock(tables[i], transaction);
                }
            }
        }

        private IEnumerable<ITable> GetRelatedTables(RelationGroup relations)
        {
            return
                relations.Referring.Select(x => x.ForeignTable)
                .Concat(relations.Referred.Select(x => x.PrimaryTable))
                .Distinct()
                .Except(new ITable[] { this }); // This table is already locked
        }

        private AtomicLogScope StartAtomicLogOperation(Transaction transaction)
        {
            return new AtomicLogScope(transaction, this.Database);
        }

        private IList<IIndex<TEntity>> FindAffectedIndexes(PropertyInfo[] changes)
        {
            IList<IIndex<TEntity>> affectedIndexes = new List<IIndex<TEntity>>();

            foreach (IIndex<TEntity> index in this.Indexes)
            {
                if (index.KeyInfo.EntityKeyMembers.Any(x => changes.Contains(x)))
                {
                    affectedIndexes.Add(index);
                }
            }
            return affectedIndexes;
        }

        private void LockRelatedTables(
            Transaction transaction,
            RelationGroup relations)
        {
            List<ITable> relatedTables = this.GetRelatedTables(relations).ToList();

            this.LockRelatedTables(transaction, relatedTables);
        }

        private void LockRelatedTables(
            Transaction transaction, 
            IEnumerable<ITable> relatedTables)
        {
            foreach (ITable table in relatedTables)
            {
                this.Database
                    .DatabaseEngine
                    .ConcurrencyManager
                    .AcquireRelatedTableLock(table, transaction);
            }
        }

        private RelationGroup FindRelations(
            IEnumerable<IIndex> indexes, 
            bool referring = true,
            bool referred = true)
        {
            RelationGroup relations = new RelationGroup();

            foreach (IIndex index in indexes)
            {
                if (referring)
                {
                    foreach (var relation in this.Database.Tables.GetReferringRelations(index))
                    {
                        relations.Referring.Add(relation);
                    }
                }

                if (referred)
                {
                    foreach (var relation in this.Database.Tables.GetReferredRelations(index))
                    {
                        relations.Referred.Add(relation);
                    }
                }
            }

            return relations;
        }

        private Dictionary<IRelation, HashSet<object>> FindReferringEntities(
            IList<TEntity> storedEntities, 
            IList<IRelationInternal> relations)
        {
            var result = new Dictionary<IRelation, HashSet<object>>();

            for (int j = 0; j < relations.Count; j++)
            {
                IRelationInternal relation = relations[j];

                HashSet<object> reffering = new HashSet<object>();

                for (int i = 0; i < storedEntities.Count; i++)
                { 
                    foreach (object entity in relation.GetReferringEntities(storedEntities[i]))
                    {
                        reffering.Add(entity);
                    }
                }

                result.Add(relation, reffering);
            }
            
            return result;
        }

        private void ValidateForeignKeys(
            IList<IRelationInternal> relations,
            Dictionary<IRelation, HashSet<object>> referringEntities)
        {
            for (int i = 0; i < relations.Count; i++)
            {
                IRelationInternal relation = relations[i];

                foreach (object entity in referringEntities[relation])
                {
                    relation.ValidateEntity(entity);
                }
            }
        }

        private void ValidateForeignKeys(
            IList<IRelationInternal> relations,
            IEnumerable<object> referringEntities)
        {
            if (relations.Count == 0)
            {
                return;
            }

            foreach (object entity in referringEntities)
            {
                for (int i = 0; i < relations.Count; i++)
                {
                    relations[i].ValidateEntity(entity);
                }
            }
        }

        private Expression<Func<TEntity, TEntity>> CreateSingleEntityUpdater(
            TEntity entity, 
            TEntity storedEntity)
        {
            List<PropertyInfo> changes = this.changeDetector.GetChanges(storedEntity, entity);

            PropertyInfo[] properties = typeof(TEntity).GetProperties();
            MemberBinding[] bindings = new MemberBinding[properties.Length];
            ParameterExpression exprParam = Expression.Parameter(typeof(TEntity));

            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                Expression source = null;

                // Check if the property was changed
                if (changes.Contains(property))
                {
                    // If so, use the new value
                    source = Expression.Constant(entity);
                }
                else
                {
                    // Otherwise, use the old value
                    source = exprParam;
                }

                bindings[i] = Expression.Bind(property, Expression.Property(source, property));
            }

            MemberInitExpression updater = 
                Expression.MemberInit(Expression.New(typeof(TEntity)), bindings);

            return Expression.Lambda<Func<TEntity, TEntity>>(updater, exprParam);
        }

        private PropertyInfo[] FindPossibleChanges(Expression<Func<TEntity, TEntity>> updater)
        {
            MemberInitExpression creator = updater.Body as MemberInitExpression;
            List<PropertyInfo> changes = new List<PropertyInfo>();

            foreach (MemberAssignment assign in creator.Bindings)
            {
                MemberExpression memberRead = assign.Expression as MemberExpression;

                // Check if the member is not assigned with the same member
                if (memberRead == null || memberRead.Member.Name != assign.Member.Name)
                {
                    changes.Add(assign.Member as PropertyInfo);
                    continue;
                }

                // Check if the source is not the parameter
                if (!(memberRead.Expression is ParameterExpression))
                {
                    changes.Add(assign.Member as PropertyInfo);
                    continue;
                }

            }

            return changes.ToArray();
        }

        private List<TEntity> CloneEntities(IList<TEntity> entities)
        {
            List<TEntity> result = new List<TEntity>();

            for (int i = 0; i < entities.Count; i++)
            {
                TEntity entity = Activator.CreateInstance<TEntity>();
                this.cloner.Clone(entities[i], entity);

                result.Add(entity);
            }

            return result;
        }
    }
}
