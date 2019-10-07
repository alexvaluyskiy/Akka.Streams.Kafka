using System;
using System.Linq;
using System.Threading.Tasks;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Dsl;
using Akka.Streams.Kafka.Helpers;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Akka.Streams.TestKit;
using Confluent.Kafka;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Streams.Kafka.Tests.Integration
{
    public class PlainPartitionedSourceIntegrationTests : KafkaIntegrationTests
    {
        public PlainPartitionedSourceIntegrationTests(ITestOutputHelper output, KafkaFixture fixture) 
            : base(nameof(PlainPartitionedSourceIntegrationTests), output, fixture)
        {
        }

        [Fact]
        public async Task PlainPartitionedSource_should_work()
        {
            var topic = CreateTopic(1);
            var group = CreateGroup(1);
            var totalMessages = 100;

            var consumerSettings = CreateConsumerSettings<string>(group);

            var control = KafkaConsumer.PlainPartitionedSource(consumerSettings, Subscriptions.Topics(topic))
                .GroupBy(3, tuple => tuple.Item1)
                .SelectAsync(8, async tuple =>
                {
                    var (topicPartition, source) = tuple;
                    Log.Info($"Sub-source for {topicPartition}");
                    var sourceMessages = await source
                        .Scan(0, (i, message) => i + 1)
                        .Select(i => LogReceivedMessages(topicPartition, i))
                        .RunWith(Sink.Last<long>(), Materializer);

                    Log.Info($"{topicPartition}: Received {sourceMessages} messages in total");
                    return sourceMessages;
                })
                .MergeSubstreams()
                .As<Source<long, IControl>>()
                .Scan(0L, (i, subValue) => i + subValue)
                .ToMaterialized(Sink.Last<long>(), Keep.Both)
                .MapMaterializedValue(tuple => DrainingControl<long>.Create(tuple.Item1, tuple.Item2))
                .Run(Materializer);
            
            await ProduceStrings(topic, Enumerable.Range(1, totalMessages), ProducerSettings);

            // Give it some time to consume all messages
            await Task.Delay(5000);

            var shutdown = control.DrainAndShutdown();
            AwaitCondition(() => shutdown.IsCompleted, TimeSpan.FromSeconds(10));
            shutdown.Result.Should().Be(totalMessages);
        }

        [Fact]
        public async Task PlainPartitionedSource_Should_split_messages_by_partitions()
        {
            var topic = CreateTopic(1);
            var group = CreateGroup(1);
            var totalMessages = 100;

            var consumerSettings = CreateConsumerSettings<string>(group);

            var control = KafkaConsumer.PlainPartitionedSource(consumerSettings, Subscriptions.Topics(topic))
                .SelectAsync(6, async tuple =>
                {
                    var (topicPartition, source) = tuple;
                    Log.Info($"Sub-source for {topicPartition}");
                    var consumedPartitions = await source
                        .Select(m => m.TopicPartition.Partition)
                        .RunWith(Sink.Seq<Partition>(), Materializer);

                    // Return flag that all messages in child source are from the same, expected partition 
                    return consumedPartitions.All(partition => partition == topicPartition.Partition);
                })
                .As<Source<bool, IControl>>()
                .ToMaterialized(Sink.Aggregate<bool, bool>(true, (result, childSourceIsValid) => result && childSourceIsValid), Keep.Both)
                .MapMaterializedValue(tuple => DrainingControl<bool>.Create(tuple.Item1, tuple.Item2))
                .Run(Materializer);
            
            await ProduceStrings(topic, Enumerable.Range(1, totalMessages), ProducerSettings);

            // Give it some time to consume all messages
            await Task.Delay(5000);

            var shutdown = control.DrainAndShutdown();
            AwaitCondition(() => shutdown.IsCompleted, TimeSpan.FromSeconds(10));
            shutdown.Result.Should().BeTrue();
        }

        /* Needs to be finished */
        /*
        [Fact]
        public async Task PlainPartitionedSource_should_stop_partition_sources_when_stopped()
        {
            var topic = CreateTopic(1);
            var group = CreateGroup(1);
            var totalMessages = 100;
            
            await ProduceStrings(topic, Enumerable.Range(1, totalMessages), ProducerSettings);

            var consumerSettings = CreateConsumerSettings<string>(group).WithStopTimeout(TimeSpan.FromMilliseconds(10));
            var (control, probe) = KafkaConsumer.PlainPartitionedSource(consumerSettings, Subscriptions.Topics(topic))
                .MergeMany<(TopicPartition, Source<ConsumeResult<Null, string>, NotUsed>), ConsumeResult<Null, string>, IControl>(1, tuple => tuple.Item2)
                .Select(message => message.Value)
                .ToMaterialized(this.SinkProbe<string>(), Keep.Both)
                .Run(Materializer);
        }
        */

        private int LogSentMessages(int counter)
        {
            if (counter % 1000 == 0)
                Log.Info($"Sent {counter} messages so far");
            
            return counter;
        }
        
        private long LogReceivedMessages(TopicPartition tp, int counter)
        {
            if (counter % 1000 == 0)
                Log.Info($"{tp}: Received {counter} messages so far.");

            return counter;
        }
    }
}