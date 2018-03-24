﻿using EventStore.ClientAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

// ReSharper disable MemberCanBePrivate.Global
namespace ReactiveDomain.Foundation.EventStore
{
    public class EventStoreRepository : IRepository
    {
        public const string EventClrQualifiedTypeHeader = "EventClrQualifiedTypeName";
        public const string EventClrTypeHeader = "EventClrTypeName";
        public const string AggregateClrTypeHeader = "AggregateClrTypeName";
        public const string AggregateClrTypeNameHeader = "AggregateClrTypeNameHeader";
        public const string CommitIdHeader = "CommitId";
        private const int WritePageSize = 500;
        private const int ReadPageSize = 500;

        private readonly IStreamNameBuilder _streamNameBuilder;
        private readonly IEventStoreConnection _eventStoreConnection;
        private static readonly JsonSerializerSettings SerializerSettings;

        static EventStoreRepository()
        {
            SerializerSettings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };
        }

        public EventStoreRepository(
            IStreamNameBuilder streamNameBuilder,
            IEventStoreConnection eventStoreConnection)
        {
            _streamNameBuilder = streamNameBuilder;
            _eventStoreConnection = eventStoreConnection;
        }

        public bool TryGetById<TAggregate>(Guid id, out TAggregate aggregate) where TAggregate : class, IEventSource
        {
            return TryGetById(id, int.MaxValue, out aggregate);
        }

        public TAggregate GetById<TAggregate>(Guid id) where TAggregate : class, IEventSource
        {
            return GetById<TAggregate>(id, int.MaxValue);
        }

        public bool TryGetById<TAggregate>(Guid id, int version, out TAggregate aggregate) where TAggregate : class, IEventSource
        {
            try
            {
                aggregate = GetById<TAggregate>(id, version);
                return true;
            }
            catch (Exception)
            {
                aggregate = null;
                return false;
            }
        }

        public TAggregate GetById<TAggregate>(Guid id, int version) where TAggregate : class, IEventSource
        {
            if (version <= 0)
                throw new InvalidOperationException("Cannot get version <= 0");

            var streamName = _streamNameBuilder.GenerateForAggregate(typeof(TAggregate), id);
            var aggregate = ConstructAggregate<TAggregate>();


            long sliceStart = 0;
            StreamEventsSlice currentSlice;
            var appliedEventCount = 0;
            do
            {
                long sliceCount = sliceStart + ReadPageSize <= version
                                    ? ReadPageSize
                                    : version - sliceStart;
                //todo: why does this need an int with v 4.0 of eventstore?
                currentSlice = _eventStoreConnection.ReadStreamEventsForwardAsync(streamName, sliceStart, (int)sliceCount, false).Result;

                if (currentSlice.Status == SliceReadStatus.StreamNotFound)
                    throw new AggregateNotFoundException(id, typeof(TAggregate));

                if (currentSlice.Status == SliceReadStatus.StreamDeleted)
                    throw new AggregateDeletedException(id, typeof(TAggregate));

                sliceStart = currentSlice.NextEventNumber;

                foreach (var evnt in currentSlice.Events)
                {
                    appliedEventCount++;
                    aggregate.RestoreFromEvent(DeserializeEvent(evnt.OriginalEvent.Metadata, evnt.OriginalEvent.Data));
                }

            } while (version > currentSlice.NextEventNumber && !currentSlice.IsEndOfStream);
            if (version != Int32.MaxValue && version != appliedEventCount)
                throw new AggregateVersionException(id, typeof(TAggregate), version, aggregate.ExpectedVersion);

            if (version != Int32.MaxValue && aggregate.ExpectedVersion != version - 1)
                throw new AggregateVersionException(id, typeof(TAggregate), version, aggregate.ExpectedVersion);

            return aggregate;

        }

        private static TAggregate ConstructAggregate<TAggregate>()
        {
            return (TAggregate)Activator.CreateInstance(typeof(TAggregate), true);
        }

        public static object DeserializeEvent(byte[] metadata, byte[] data)
        {
            var settings = new JsonSerializerSettings { ContractResolver = new ContractResolver() };
            var eventClrTypeName = JObject.Parse(Encoding.UTF8.GetString(metadata)).Property(EventClrQualifiedTypeHeader).Value; // todo: fallback to using type name optionnaly
            return JsonConvert.DeserializeObject(Encoding.UTF8.GetString(data), Type.GetType((string)eventClrTypeName), settings);
        }

        public void Save(IEventSource aggregate)
        {
            var commitHeaders = new Dictionary<string, object>
            {
                {CommitIdHeader, Guid.NewGuid() /*commitId*/},
                {AggregateClrTypeNameHeader, aggregate.GetType().AssemblyQualifiedName},
                {AggregateClrTypeHeader, aggregate.GetType().Name}
            };

            var streamName = _streamNameBuilder.GenerateForAggregate(aggregate.GetType(), aggregate.Id);
            var newEvents = aggregate.TakeEvents().ToList();
            var expectedVersion = aggregate.ExpectedVersion;
            var eventsToSave = newEvents.Select(e => ToEventData(Guid.NewGuid(), e, commitHeaders)).ToList();

            if (eventsToSave.Count < WritePageSize)
            {
                _eventStoreConnection.AppendToStreamAsync(streamName, expectedVersion, eventsToSave).Wait();
            }
            else
            {
                var transaction = _eventStoreConnection.StartTransactionAsync(streamName, expectedVersion).Result;

                var position = 0;
                while (position < eventsToSave.Count)
                {
                    var pageEvents = eventsToSave.Skip(position).Take(WritePageSize);
                    transaction.WriteAsync(pageEvents).Wait();
                    position += WritePageSize;
                }

                transaction.CommitAsync().Wait();
            }
            //aggregate.ClearUncommittedEvents();
        }

        public static EventData ToEventData(Guid eventId, object evnt, IDictionary<string, object> headers)
        {
            var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(evnt, SerializerSettings));

            var eventHeaders = new Dictionary<string, object>(headers)
            {
                {EventClrTypeHeader, evnt.GetType().Name},
                {EventClrQualifiedTypeHeader, evnt.GetType().AssemblyQualifiedName}
            };
            var metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(eventHeaders, SerializerSettings));
            var typeName = evnt.GetType().Name;

            return new EventData(eventId, typeName, true, data, metadata);
        }
    }
    public class ContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            property.Writable = CanSetMemberValue(member, true);
            return property;
        }

        public static bool CanSetMemberValue(MemberInfo member, bool nonPublic)
        {
            switch (member.MemberType)
            {
                case MemberTypes.Field:
                    var fieldInfo = (FieldInfo)member;

                    return nonPublic || fieldInfo.IsPublic;
                case MemberTypes.Property:
                    var propertyInfo = (PropertyInfo)member;

                    if (!propertyInfo.CanWrite)
                        return false;
                    if (nonPublic)
                        return true;
                    // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                    return (propertyInfo.GetSetMethod(nonPublic) != null);
                default:
                    return false;
            }
        }
    }
}
