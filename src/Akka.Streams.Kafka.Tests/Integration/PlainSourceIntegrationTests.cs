﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Akka.Configuration;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Dsl;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Akka.Streams.Kafka.Tests.Logging;
using Akka.Streams.Supervision;
using Akka.Streams.TestKit;
using Confluent.Kafka;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using Config = Akka.Configuration.Config;

namespace Akka.Streams.Kafka.Tests.Integration
{
    public class PlainSourceIntegrationTests : KafkaIntegrationTests
    {
        private const string InitialMsg = "initial msg in topic, required to create the topic before any consumer subscribes to it";
        
        private readonly KafkaFixture _fixture;
        private readonly ActorMaterializer _materializer;

        public PlainSourceIntegrationTests(ITestOutputHelper output, KafkaFixture fixture) 
            : base(nameof(PlainSourceIntegrationTests), output)
        {
            _fixture = fixture;
            _materializer = Sys.Materializer();
        }

        private string Uuid { get; } = Guid.NewGuid().ToString();

        private string CreateTopic(int number) => $"topic-{number}-{Uuid}";
        private string CreateGroup(int number) => $"group-{number}-{Uuid}";

        private ProducerSettings<Null, string> ProducerSettings
        {
            get => ProducerSettings<Null, string>.Create(Sys, null, null).WithBootstrapServers(_fixture.KafkaServer);
        }

        private ConsumerSettings<Null, string> CreateConsumerSettings(string group)
        {
            return ConsumerSettings<Null, string>.Create(Sys, null, null)
                .WithBootstrapServers(_fixture.KafkaServer)
                .WithProperty("auto.offset.reset", "earliest")
                .WithGroupId(group);
        }
        
        private async Task GivenInitializedTopic(string topic)
        {
            using (var producer = ProducerSettings.CreateKafkaProducer())
            {
                await producer.ProduceAsync(topic, new Message<Null, string> { Value = InitialMsg });
                producer.Flush(TimeSpan.FromSeconds(1));
            }
        }

        private async Task Produce(string topic, IEnumerable<int> range, ProducerSettings<Null, string> producerSettings)
        {
            await Source
                .From(range)
                .Select(elem => new MessageAndMeta<Null, string> { Topic = topic, Message = new Message<Null, string> { Value = elem.ToString() } })
                .RunWith(KafkaProducer.PlainSink(producerSettings), _materializer);
        }

        private TestSubscriber.Probe<string> CreateProbe(ConsumerSettings<Null, string> consumerSettings, ISubscription sub)
        {
            return KafkaConsumer
                .PlainSource(consumerSettings, sub)
                .Where(c => !c.Value.Equals(InitialMsg))
                .Select(c => c.Value)
                .RunWith(this.SinkProbe<string>(), _materializer);
        }

        [Fact]
        public async Task PlainSource_consumes_messages_from_KafkaProducer_with_topicPartition_assignment()
        {
            int elementsCount = 100;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var consumerSettings = CreateConsumerSettings(group1);

            var probe = CreateProbe(consumerSettings, Subscriptions.Assignment(new TopicPartition(topic1, 0)));
            
            probe.Request(elementsCount);
            foreach (var i in Enumerable.Range(1, elementsCount).Select(c => c.ToString()))
                probe.ExpectNext(i, TimeSpan.FromSeconds(10));

            probe.Cancel();
        }

        [Fact]
        public async Task PlainSource_consumes_messages_from_KafkaProducer_with_topicPartitionOffset_assignment()
        {
            int elementsCount = 100;
            int offset = 50;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var consumerSettings = CreateConsumerSettings(group1);

            var probe = CreateProbe(consumerSettings, Subscriptions.AssignmentWithOffset(new TopicPartitionOffset(topic1, 0, new Offset(offset))));

            probe.Request(elementsCount);
            foreach (var i in Enumerable.Range(offset, elementsCount - offset).Select(c => c.ToString()))
                probe.ExpectNext(i, TimeSpan.FromSeconds(10000));

            probe.Cancel();
        }

        [Fact]
        public async Task PlainSource_consumes_messages_from_KafkaProducer_with_subscribe_to_topic()
        {
            int elementsCount = 100;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var consumerSettings = CreateConsumerSettings(group1);

            var probe = CreateProbe(consumerSettings, Subscriptions.Topics(topic1));

            probe.Request(elementsCount);
            foreach (var i in Enumerable.Range(1, elementsCount).Select(c => c.ToString()))
                probe.ExpectNext(i, TimeSpan.FromSeconds(10));

            probe.Cancel();
        }

        [Fact]
        public async Task PlainSource_should_fail_stage_if_broker_unavailable()
        {
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await GivenInitializedTopic(topic1);

            var config = ConsumerSettings<Null, string>.Create(Sys, null, null)
                .WithBootstrapServers("localhost:10092")
                .WithGroupId(group1);

            var probe = CreateProbe(config, Subscriptions.Assignment(new TopicPartition(topic1, 0)));
            probe.Request(1).ExpectError().Should().BeOfType<KafkaException>();
        }

        [Fact]
        public async Task PlainSource_should_stop_on_deserialization_errors()
        {
            int elementsCount = 10;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var settings = ConsumerSettings<Null, int>.Create(Sys, null, Deserializers.Int32)
                .WithBootstrapServers(_fixture.KafkaServer)
                .WithProperty("auto.offset.reset", "earliest")
                .WithGroupId(group1);

            var probe = KafkaConsumer
                .PlainSource(settings, Subscriptions.Assignment(new TopicPartition(topic1, 0)))
                .WithAttributes(ActorAttributes.CreateSupervisionStrategy(Deciders.StoppingDecider))
                .Select(c => c.Value)
                .RunWith(this.SinkProbe<int>(), _materializer);

            var error = probe.Request(elementsCount).ExpectEvent(TimeSpan.FromSeconds(10));
            error.Should().BeOfType<TestSubscriber.OnError>();
            ((TestSubscriber.OnError)error).Cause.Should().BeOfType<SerializationException>();
            probe.Cancel();
        }

        [Fact]
        public async Task PlainSource_should_resume_on_deserialization_errors()
        {
            Directive Decider(Exception cause) => cause is SerializationException
                ? Directive.Resume
                : Directive.Stop;

            int elementsCount = 10;
            var topic1 = CreateTopic(1);
            var group1 = CreateGroup(1);

            await Produce(topic1, Enumerable.Range(1, elementsCount), ProducerSettings);

            var settings = ConsumerSettings<Null, int>.Create(Sys, null, Deserializers.Int32)
                .WithBootstrapServers(_fixture.KafkaServer)
                .WithProperty("auto.offset.reset", "earliest")
                .WithGroupId(group1);

            var probe = KafkaConsumer
                .PlainSource(settings, Subscriptions.Assignment(new TopicPartition(topic1, 0)))
                .WithAttributes(ActorAttributes.CreateSupervisionStrategy(Decider))
                .Select(c => c.Value)
                .RunWith(this.SinkProbe<int>(), _materializer);

            probe.Request(elementsCount);
            probe.ExpectNoMsg(TimeSpan.FromSeconds(10));
            probe.Cancel();
        }
    }
}
