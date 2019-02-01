﻿using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace DefaultEcs.System
{
    /// <summary>
    /// Represents a base class to process updates on a given <see cref="EntitySet"/> instance.
    /// </summary>
    /// <typeparam name="T">The type of the object used as state to update the system.</typeparam>
    public abstract class AEntitySystem<T> : ASystem<T>
    {
        #region Fields

        private static readonly ConcurrentDictionary<Type, Func<World, EntitySet>> _entitySetFactories = new ConcurrentDictionary<Type, Func<World, EntitySet>>();

        private readonly EntitySet _set;

        #endregion

        #region Initialisation

        /// <summary>
        /// Initialise a new instance of the <see cref="AEntitySystem{T}"/> class with the given <see cref="EntitySet"/> and <see cref="SystemRunner{T}"/>.
        /// </summary>
        /// <param name="set">The <see cref="EntitySet"/> on which to process the update.</param>
        /// <param name="runner">The <see cref="SystemRunner{T}"/> used to process the update in parallel if not null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
        protected AEntitySystem(EntitySet set, SystemRunner<T> runner)
            : base(runner)
        {
            _set = set ?? throw new ArgumentNullException(nameof(set));
        }

        /// <summary>
        /// Initialise a new instance of the <see cref="AEntitySystem{T}"/> class with the given <see cref="EntitySet"/>.
        /// </summary>
        /// <param name="set">The <see cref="EntitySet"/> on which to process the update.</param>
        /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
        protected AEntitySystem(EntitySet set)
            : this(set, null)
        { }

        /// <summary>
        /// Initialise a new instance of the <see cref="AEntitySystem{T}"/> class with the given <see cref="World"/>.
        /// To create the inner <see cref="EntitySet"/>, <see cref="WithAttribute"/> and <see cref="WithoutAttribute"/> attributes will be used.
        /// </summary>
        /// <param name="world">The <see cref="World"/> from which to get the <see cref="Entity"/> instances to process the update.</param>
        /// <param name="runner">The <see cref="SystemRunner{T}"/> used to process the update in parallel if not null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
        protected AEntitySystem(World world, SystemRunner<T> runner)
            : base(runner)
        {
            _set = _entitySetFactories.GetOrAdd(GetType(), GetEntitySetFactory)(world ?? throw new ArgumentNullException(nameof(world)));
        }

        /// <summary>
        /// Initialise a new instance of the <see cref="AEntitySystem{T}"/> class with the given <see cref="World"/>.
        /// To create the inner <see cref="EntitySet"/>, <see cref="WithAttribute"/> and <see cref="WithoutAttribute"/> attributes will be used.
        /// </summary>
        /// <param name="world">The <see cref="World"/> from which to get the <see cref="Entity"/> instances to process the update.</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
        protected AEntitySystem(World world)
            : this(world, null)
        { }

        #endregion

        #region Methods

        private static Func<World, EntitySet> GetEntitySetFactory(Type type)
        {
            TypeInfo entitySetBuilder = typeof(EntitySetBuilder).GetTypeInfo();
            MethodInfo with = entitySetBuilder.GetDeclaredMethod(nameof(EntitySetBuilder.With));
            MethodInfo without = entitySetBuilder.GetDeclaredMethod(nameof(EntitySetBuilder.Without));
            MethodInfo withAny = entitySetBuilder.GetDeclaredMethod(nameof(EntitySetBuilder.WithAny));

            ParameterExpression world = Expression.Parameter(typeof(World));
            Expression expression = Expression.Call(world, typeof(World).GetTypeInfo().GetDeclaredMethod(nameof(World.GetEntities)));

            foreach (ComponentAttribute attribute in type.GetTypeInfo().GetCustomAttributes<ComponentAttribute>(true))
            {
                switch (attribute.FilterType)
                {
                    case ComponentFilterType.With:
                        foreach (Type componentType in attribute.ComponentTypes)
                        {
                            expression = Expression.Call(expression, with.MakeGenericMethod(componentType));
                        }
                        break;

                    case ComponentFilterType.Without:
                        foreach (Type componentType in attribute.ComponentTypes)
                        {
                            expression = Expression.Call(expression, without.MakeGenericMethod(componentType));
                        }
                        break;

                    case ComponentFilterType.WithAny:
                        expression = Expression.Call(expression, withAny, Expression.Constant(attribute.ComponentTypes));
                        break;
                }
            }

            expression = Expression.Call(expression, entitySetBuilder.GetDeclaredMethod(nameof(EntitySetBuilder.Build)));

            return Expression.Lambda<Func<World, EntitySet>>(expression, world).Compile();
        }

        /// <summary>
        /// Update the given <see cref="Entity"/> instance once.
        /// </summary>
        /// <param name="state">The state to use.</param>
        /// <param name="entity">The <see cref="Entity"/> instance to update.</param>
        protected virtual void Update(T state, in Entity entity) { }

        /// <summary>
        /// Update the given <see cref="Entity"/> instances once.
        /// </summary>
        /// <param name="state">The state to use.</param>
        /// <param name="entities">The <see cref="Entity"/> instances to update.</param>
#pragma warning disable RCS1231 // Make parameter ref read-only. For some reason, less performant with a "in"
        protected virtual void Update(T state, ReadOnlySpan<Entity> entities)
#pragma warning restore RCS1231 // Make parameter ref read-only.
        {
            for (int i = 0; i < entities.Length; ++i)
            {
                Update(state, entities[i]);
            }
        }

        #endregion

        #region ASystem

        /// <summary>
        /// Gets or sets whether the current <see cref="AEntitySystem{T}"/> instance should update or not.
        /// Will return false is there is no <see cref="Entity"/> to update.
        /// </summary>
        public sealed override bool IsEnabled
        {
            get { return base.IsEnabled && _set.IsNotEmpty; }
            set { base.IsEnabled = value; }
        }

        internal sealed override void Update(int index, int maxIndex)
        {
            ReadOnlySpan<Entity> entities = _set.GetEntities();
            int entitiesToUpdate = entities.Length / (maxIndex + 1);

            if (index == maxIndex)
            {
                Update(CurrentState, index == 0 ? entities : entities.Slice(index * entitiesToUpdate));
            }
            else
            {
                Update(CurrentState, entities.Slice(index * entitiesToUpdate, entitiesToUpdate));
            }
        }

        /// <summary>
        /// Disposes of the inner <see cref="EntitySet"/> instance.
        /// </summary>
        public override void Dispose()
        {
            _set.Dispose();
        }

        #endregion
    }
}
