using Microsoft.Extensions.Hosting.Internal;
using Bot.AspNetCore;
using WorksPad.Assistant.Bot;
using WorksPad.Assistant.Bot.Protocol.BotServer;
using Bot.Lib;
using Serilog;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

ServiceCollection serviceCollection = new ServiceCollection();
serviceCollection.AddLogging();
ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();
MyChatBotConfiguration BotConfig = builder.Configuration.Get<MyChatBotConfiguration>();
ChatBot chatBot = new ChatBot();

bool IgnoreCertificateErrors(
    object sender,
    X509Certificate certificate,
    X509Chain chain,
    SslPolicyErrors sslPolicyErrors)
{
    // Игнорировать все ошибки сертификата
    return true;
}

var httpClientHandler = new HttpClientHandler();

httpClientHandler.ServerCertificateCustomValidationCallback = IgnoreCertificateErrors;
//httpClientHandler.ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>{ return true; };

ChatBotCommunicator chatBotCommunicator = new ChatBotCommunicator(BotConfig, chatBot, httpClientHandler);
builder.Services.AddSingleton(chatBotCommunicator);
builder.WebHost.ConfigureKestrel((context, serverOptions) =>
{
    var kestrelSection = context.Configuration.GetSection("Kestrel");
});
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.Lifetime.ApplicationStarted.Register(async () => await _ReactivateBotAsync(chatBotCommunicator, BotConfig.ChatBotUrl));
app.Lifetime.ApplicationStopping.Register(async () => await _DeactivateBotAsync(chatBotCommunicator));

async Task _DeactivateBotAsync(ChatBotCommunicator chatBotCommunicator)
{
    var requestDeactivateBotModel = new RequestDeactivateBotModel();
    await chatBotCommunicator.DeactivateBotAsync(requestDeactivateBotModel);
}

async Task _ReactivateBotAsync(ChatBotCommunicator chatBotCommunicator, string chatBotUrl)
{
    int chatBotOrderIndex = 0;
    var chatBotCommandList = new List<RequestActivateBotModel.BotCommand>()
    {
        new RequestActivateBotModel.BotCommand(
            ChatBotCommand.get_info,
            "Получить информацию о статусах",
            "Команда управления просмотром информации",
            chatBotOrderIndex++)
    };
    var requestActivateBotModel = new RequestActivateBotModel(
                                        chatBotUrl,
                                        false
                                        , chatBotCommandList
                                        );
    await chatBotCommunicator.ReactivateBotAsync(requestActivateBotModel);
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
