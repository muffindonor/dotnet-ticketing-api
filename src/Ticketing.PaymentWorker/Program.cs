using Ticketing.PaymentWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<PaymentRequestConsumer>();
await builder.Build().RunAsync();
