﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using DefaultEcs.Technical.Command;

namespace DefaultEcs.Command
{
    public sealed unsafe class EntityCommandRecorder
    {
        private enum CommandType : byte
        {
            Entity = 0,
            CreateEntity = 1,
            Enable = 2,
            Disable = 3,
            EnableT = 4,
            DisableT = 5,
            Set = 6,
            SetSameAs = 7,
            Remove = 8,
            SetAsChildOf = 9,
            RemoveFromChildrenOf = 10,
            Dispose = 11
        }

        private interface IComponentCommand
        {
            void Enable(in Entity entity);
            void Disable(in Entity entity);
            void Set(List<object> objects, byte* memory, int* data);
            void SetSameAs(byte* memory, int* data);
            void Remove(in Entity entity);
        }

        private class ComponentCommand<T> : IComponentCommand
        {
            private static readonly ComponentCommandCreateSet<T> _createSetAction;
            private static readonly ComponentCommandSet _setAction;

#pragma warning disable RCS1158 // Static member in generic type should use a type parameter.
            public static readonly int Index;
            public static readonly int SizeOfT;
#pragma warning restore RCS1158 // Static member in generic type should use a type parameter.

            static ComponentCommand()
            {
                lock (_componentCommands)
                {
                    Index = _componentCommands.Count;
                    _componentCommands.Add(new ComponentCommand<T>());
                }

                try
                {
                    TypeInfo typeInfo = typeof(UnmanagedComponentCommand<>)
                        .MakeGenericType(typeof(T))
                        .GetTypeInfo();

                    SizeOfT = (int)typeInfo.GetDeclaredField(nameof(UnmanagedComponentCommand<bool>.SizeOfT)).GetValue(null);

                    _createSetAction = (ComponentCommandCreateSet<T>)typeInfo
                        .GetDeclaredMethod(nameof(UnmanagedComponentCommand<bool>.CreateSet))
                        .CreateDelegate(typeof(ComponentCommandCreateSet<T>));
                    _setAction = (ComponentCommandSet)typeInfo
                        .GetDeclaredMethod(nameof(UnmanagedComponentCommand<bool>.Set))
                        .CreateDelegate(typeof(ComponentCommandSet));
                }
                catch
                {
                    SizeOfT = sizeof(int);

                    _createSetAction = ManagedComponentCommand<T>.CreateSet;
                    _setAction = ManagedComponentCommand<T>.Set;
                }
            }

            public static void CreateSet(List<object> objects, int* memory, in T component) => _createSetAction(objects, memory, component);

            public void Enable(in Entity entity) => entity.Enable<T>();

            public void Disable(in Entity entity) => entity.Disable<T>();

            public void Set(List<object> objects, byte* memory, int* data) => _setAction(objects, memory, data);

            public void SetSameAs(byte* memory, int* data) => (*(Entity*)(memory + *data++)).SetSameAs<T>(*(Entity*)(memory + *data));

            public void Remove(in Entity entity) => entity.Remove<T>();
        }

        private const int _baseCommandSize = sizeof(int) + sizeof(byte);
        private const int _baseComponentCommandSize = _baseCommandSize + sizeof(int);

        private static readonly List<IComponentCommand> _componentCommands;
        private static readonly ConcurrentDictionary<CommandType, int> _commandSizes;

        private readonly byte[] _memory;
        private readonly List<object> _objects;

        private int _nextCommandOffset;

        static EntityCommandRecorder()
        {
            _componentCommands = new List<IComponentCommand>();
            _commandSizes = new ConcurrentDictionary<CommandType, int>
            {
                [CommandType.Entity] = _baseCommandSize + sizeof(Entity),
                [CommandType.CreateEntity] = _baseCommandSize + sizeof(Entity),
                [CommandType.Enable] = _baseCommandSize + sizeof(int),
                [CommandType.Disable] = _baseCommandSize + sizeof(int),
                [CommandType.EnableT] = _baseComponentCommandSize + sizeof(int),
                [CommandType.DisableT] = _baseComponentCommandSize + sizeof(int),
                [CommandType.Set] = _baseComponentCommandSize + sizeof(int),
                [CommandType.SetSameAs] = _baseComponentCommandSize + sizeof(int) + sizeof(int),
                [CommandType.Remove] = _baseComponentCommandSize + sizeof(int),
                [CommandType.SetAsChildOf] = _baseCommandSize + sizeof(int) + sizeof(int),
                [CommandType.RemoveFromChildrenOf] = _baseCommandSize + sizeof(int) + sizeof(int),
                [CommandType.Dispose] = _baseCommandSize + sizeof(int),
            };
        }

        public EntityCommandRecorder(int size)
        {
            _memory = new byte[size];
            _objects = new List<object>();
            _nextCommandOffset = 0;
        }

        private byte* ReserveNextCommand(byte* memory, CommandType commandType, out int commandOffset, int extraSize = 0)
        {
            void Throw() => throw new Exception("CommandBuffer is full.");

            int commandSize = _commandSizes[commandType] + extraSize;

            do
            {
                commandOffset = _nextCommandOffset;
                if (commandOffset > _memory.Length)
                {
                    Throw();
                }
            }
            while (Interlocked.CompareExchange(ref _nextCommandOffset, commandOffset + commandSize, commandOffset) != commandOffset);

            int* nextCommandP = (int*)(memory + commandOffset);
            *nextCommandP++ = commandSize;
            byte* commandTypeP = (byte*)nextCommandP;
            *commandTypeP++ = (byte)commandType;

            return commandTypeP;
        }

        private byte* ReserveNextCommand(byte* memory, CommandType commandType, int extraSize = 0) => ReserveNextCommand(memory, commandType, out _, extraSize);

        private byte* ReserveNextCommand<T>(byte* memory, CommandType commandType, int extraSize = 0)
        {
            int* componentCommandIndexP = (int*)ReserveNextCommand(memory, commandType, out _, extraSize);
            *componentCommandIndexP++ = ComponentCommand<T>.Index;

            return (byte*)componentCommandIndexP;
        }

        internal void Enable(int entityOffset)
        {
            fixed (byte* memory = _memory)
            {
                *(int*)ReserveNextCommand(memory, CommandType.Enable) = entityOffset;
            }
        }

        internal void Disable(int entityOffset)
        {
            fixed (byte* memory = _memory)
            {
                *(int*)ReserveNextCommand(memory, CommandType.Disable) = entityOffset;
            }
        }

        internal void Enable<T>(int entityOffset)
        {
            fixed (byte* memory = _memory)
            {
                *(int*)ReserveNextCommand<T>(memory, CommandType.EnableT) = entityOffset;
            }
        }

        internal void Disable<T>(int entityOffset)
        {
            fixed (byte* memory = _memory)
            {
                *(int*)ReserveNextCommand<T>(memory, CommandType.DisableT) = entityOffset;
            }
        }

        internal void Set<T>(int entityOffset, in T component)
        {
            fixed (byte* memory = _memory)
            {
                int* entityP = (int*)ReserveNextCommand<T>(memory, CommandType.Set, ComponentCommand<T>.SizeOfT);
                *entityP++ = entityOffset;
                ComponentCommand<T>.CreateSet(_objects, entityP, component);
            }
        }

        internal void SetSameAs<T>(int entityOffset, int referenceOffset)
        {
            fixed (byte* memory = _memory)
            {
                int* entityP = (int*)ReserveNextCommand<T>(memory, CommandType.SetSameAs);
                *entityP++ = entityOffset;
                *entityP = referenceOffset;
            }
        }

        internal void Remove<T>(int entityOffset)
        {
            fixed (byte* memory = _memory)
            {
                *(int*)ReserveNextCommand<T>(memory, CommandType.Remove) = entityOffset;
            }
        }

        internal void SetAsChildOf(int chidOffset, int parentOffset)
        {
            fixed (byte* memory = _memory)
            {
                int* entityP = (int*)ReserveNextCommand(memory, CommandType.SetAsChildOf);
                *entityP++ = chidOffset;
                *entityP = parentOffset;
            }
        }

        internal void RemoveFromChildrenOf(int chidOffset, int parentOffset)
        {
            fixed (byte* memory = _memory)
            {
                int* entityP = (int*)ReserveNextCommand(memory, CommandType.RemoveFromChildrenOf);
                *entityP++ = chidOffset;
                *entityP = parentOffset;
            }
        }

        internal void Dispose(int entityOffset)
        {
            fixed (byte* memory = _memory)
            {
                *(int*)ReserveNextCommand(memory, CommandType.Dispose) = entityOffset;
            }
        }

        public EntityRecord Record(in Entity entity)
        {
            fixed (byte* memory = _memory)
            {
                *(Entity*)ReserveNextCommand(memory, CommandType.Entity, out int offset) = entity;

                return new EntityRecord(this, offset + _baseCommandSize);
            }
        }

        public EntityRecord CreateEntity()
        {
            fixed (byte* memory = _memory)
            {
                ReserveNextCommand(memory, CommandType.CreateEntity, out int offset);

                return new EntityRecord(this, offset + _baseCommandSize);
            }
        }

        public void Execute(World world)
        {
            fixed (byte* memory = _memory)
            {
                byte* commands = memory;
                while (_nextCommandOffset > 0)
                {
                    int commandSize = *(int*)commands;
                    switch (*(CommandType*)(commands + sizeof(int)))
                    {
                        case CommandType.CreateEntity:
                            *(Entity*)(commands + _baseCommandSize) = world.CreateEntity();
                            break;

                        case CommandType.Enable:
                            (*(Entity*)(memory + *(int*)(commands + _baseCommandSize))).Enable();
                            break;

                        case CommandType.Disable:
                            (*(Entity*)(memory + *(int*)(commands + _baseCommandSize))).Disable();
                            break;

                        case CommandType.EnableT:
                            _componentCommands[*(int*)(commands + _baseCommandSize)].Enable(*(Entity*)(memory + *(int*)(commands + _baseComponentCommandSize)));
                            break;

                        case CommandType.DisableT:
                            _componentCommands[*(int*)(commands + _baseCommandSize)].Disable(*(Entity*)(memory + *(int*)(commands + _baseComponentCommandSize)));
                            break;

                        case CommandType.Set:
                            _componentCommands[*(int*)(commands + _baseCommandSize)].Set(_objects, memory, (int*)(commands + _baseComponentCommandSize));
                            break;

                        case CommandType.SetSameAs:
                            _componentCommands[*(int*)(commands + _baseCommandSize)].SetSameAs(memory, (int*)(commands + _baseComponentCommandSize));
                            break;

                        case CommandType.Remove:
                            _componentCommands[*(int*)(commands + _baseCommandSize)].Remove(*(Entity*)(memory + *(int*)(commands + _baseComponentCommandSize)));
                            break;

                        case CommandType.SetAsChildOf:
                            (*(Entity*)(memory + *(int*)(commands + _baseCommandSize))).SetAsChildOf(*(Entity*)(memory + *(int*)(commands + _baseCommandSize + sizeof(int))));
                            break;

                        case CommandType.RemoveFromChildrenOf:
                            (*(Entity*)(memory + *(int*)(commands + _baseCommandSize))).RemoveFromChildrenOf(*(Entity*)(memory + *(int*)(commands + _baseCommandSize + sizeof(int))));
                            break;

                        case CommandType.Dispose:
                            (*(Entity*)(memory + *(int*)(commands + _baseComponentCommandSize))).Dispose();
                            break;
                    }

                    commands += commandSize;
                    _nextCommandOffset -= commandSize;
                }

                _objects.Clear();
            }
        }
    }
}
