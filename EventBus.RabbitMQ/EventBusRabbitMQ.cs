using EventBus.Base;
using EventBus.Base.Events;
using Newtonsoft.Json;
using Polly;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace EventBus.RabbitMQ
{
    public class EventBusRabbitMQ : BaseEventBus
    {
        RabbitMQPersistenConnection rabbitMQPersistenConnection;

        IConnectionFactory connectionFactory;
        IModel consumerChannel;
        public EventBusRabbitMQ(EventBusConfig eventBusConfig, IServiceProvider serviceProvider) : base(eventBusConfig, serviceProvider)
        {
            if (eventBusConfig.Connection != null)
            {
                var connJson = JsonConvert.SerializeObject(eventBusConfig, new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });
                connectionFactory = JsonConvert.DeserializeObject<ConnectionFactory>(connJson);
            }
            else
                connectionFactory = new ConnectionFactory();
            rabbitMQPersistenConnection = new RabbitMQPersistenConnection(connectionFactory, eventBusConfig.ConnectionRetryCount);
            consumerChannel = CreateComsumerChannel();
            SubsManager.OnEventRemoved += SubsManager_OnEventRemoved;
        }

        private void SubsManager_OnEventRemoved(object sender, string eventName)
        {
            eventName = ProccessEventName(eventName);
            if (!rabbitMQPersistenConnection.IsConncected)
            {
                rabbitMQPersistenConnection.TryConnect();
            }
            consumerChannel.QueueBind(queue: GetSubName(eventName), exchange: eventBusConfig.DefaultTopicName, routingKey: eventName);

            if (SubsManager.IsEmpty)
            {
                consumerChannel.Close();
            }
        }

        public override void Publis(IntegrationEvent @event)
        {
            if (!rabbitMQPersistenConnection.IsConncected)
            {
                rabbitMQPersistenConnection.TryConnect();
            }

            var policy = Policy.Handle<SocketException>().Or<BrokerUnreachableException>()
                       .WaitAndRetry(eventBusConfig.ConnectionRetryCount, retryAttemp => TimeSpan.FromSeconds(Math.Pow(2, retryAttemp)), (ex, time) =>
                       {
                           //log here
                       });

            var eventName = @event.GetType().Name;

            eventName = ProccessEventName(eventName);

            consumerChannel.ExchangeDeclare(exchange: eventBusConfig.DefaultTopicName, type: "direct");

            var message = JsonConvert.SerializeObject(@event);
            var body = Encoding.UTF8.GetBytes(message);

            policy.Execute(() =>
            {
                var properties = consumerChannel.CreateBasicProperties();

                properties.DeliveryMode = 2;

              //  consumerChannel.QueueDeclare(queue: GetSubName(eventName), durable: true, exclusive: false, autoDelete: false, arguments: null);

                consumerChannel.BasicPublish(exchange: eventBusConfig.DefaultTopicName, routingKey: eventName, mandatory: true, basicProperties: properties, body: body);
            });
        }

        public override void Subscribe<T, TH>()
        {
            var eventName = typeof(T).Name;
            eventName = ProccessEventName(eventName);

            if (!SubsManager.HasSubscriptionsForEvent(eventName))
            {
                if (!rabbitMQPersistenConnection.IsConncected)
                {
                    rabbitMQPersistenConnection.TryConnect();
                }

                consumerChannel.QueueDeclare(queue: GetSubName(eventName),
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                consumerChannel.QueueBind(queue: GetSubName(eventName), exchange: eventBusConfig.DefaultTopicName, routingKey: eventName);
            }
            SubsManager.AddSubscription<T, TH>();
            StartBasicConsumer(eventName);
        }

        public override void UnSubscribe<T, TH>()
        {
            SubsManager.RemoveSubscription<T, TH>();
        }

        private IModel CreateComsumerChannel()
        {
            if (!rabbitMQPersistenConnection.IsConncected)
            {
                rabbitMQPersistenConnection.TryConnect();
            }
            var channel = rabbitMQPersistenConnection.CreateModel();

            channel.ExchangeDeclare(exchange: eventBusConfig.DefaultTopicName, type: "direct");

            return channel;
        }

        private void  StartBasicConsumer(string eventName)
        {
            if (consumerChannel != null)
            {
                var consumer = new EventingBasicConsumer(consumerChannel);
                consumer.Received +=  Consumer_Received;
                consumerChannel.BasicConsume(queue: GetSubName(eventName), autoAck: false, consumer: consumer);
            }
        }

        private async void Consumer_Received(object sender, BasicDeliverEventArgs e)
        {
            var eventName = e.RoutingKey;

            eventName = ProccessEventName(eventName);

            var message = Encoding.UTF8.GetString(e.Body.Span);

            try
            {
                await ProcessEvent(eventName, message);
            }
            catch (Exception)
            {
            }
            consumerChannel.BasicAck(e.DeliveryTag, false);
        }
    }
}
