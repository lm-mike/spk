using System;
using System.IO;
using System.Data;
using System.Data.SqlClient;
using Newtonsoft.Json.Linq;
using WorksPad.Assistant.Bot;
using WorksPad.Assistant.Bot.Protocol;
using WorksPad.Assistant.Bot.Protocol.BotServer;
using WorksPad.Assistant.Bot.Protocol.ServerBot;
using WorksPad.Assistant.ExternalService.Http;
using Bot.Lib.AdaptiveCard;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Bot.Models;
using System.Collections;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Serilog;
using Dapper;

namespace Bot.Lib
{
    public class ChatBot : IServerRequestHandler
    {
        public DateTime maintime;
        //private DefaultValues defValues;
        public ChatBot() //DefaultValues _defValues
        {
            //this.defValues = _defValues;
        }

        public async Task<ResponseResult> ButtonClickedAsync(ChatBotCommunicator communicator, RequestButtonClickedModel requestModel, CancellationToken cancellationToken)
        {
            RequestUpdateMessageModel messageForClient;
            try
            {
                Log.Information("Button clicked");
                var dataContent = JObject.Parse(requestModel.CallbackData.ToString());
                var cmd = dataContent.GetValue("cmd").ToString();
                Log.Information($"Data content: {dataContent}");
                switch (cmd)
                {
                    case "checkInfo":
                        messageForClient = await ProcessCommandRequest(requestModel, dataContent);
                        break;
                    case "InputNewCommand":
                        messageForClient = await ProcessNewCommandRequest(requestModel);
                        break;
                    default:
                        Log.Warning("cmd was not found");
                        messageForClient = new RequestUpdateMessageModel(
                            requestModel.Channel,
                            requestModel.Conversation.Id,
                            requestModel.MessageId,
                            MessageTextFormat.Plain,
                            "Не удалось обработать команду");
                        break;
                }
            }
            catch
            {
                messageForClient = new RequestUpdateMessageModel(
                    requestModel.Channel,
                    requestModel.Conversation.Id,
                    requestModel.MessageId,
                    MessageTextFormat.Plain,
                    "Не удалось обработать команду");
            }

            if (messageForClient != null)
            {
                if (requestModel.Channel == Channel.WorksPadAssistant)
                {
                    await communicator.UpdateMessageAsync(messageForClient, cancellationToken);
                }
                else
                {
                    await communicator.SendMessageAsync(new RequestSendMessageModel(
                        messageForClient.Channel,
                        messageForClient.ConversationId,
                        messageForClient.TextFormat,
                        messageForClient.Text,
                        messageForClient.ButtonList,
                        messageForClient.AttachmentLayout,
                        messageForClient.AttachmentList),
                        cancellationToken);
                }
            }
            return ResponseResult.Ok();
        }

        private async Task<RequestUpdateMessageModel> ProcessNewCommandRequest(RequestButtonClickedModel requestModel)
        {
            Attachment attachment;
            attachment = AdaptiveCardHelper.CreateUpdateAttachment("MainInputTemplate.json");
            return new RequestUpdateMessageModel(
                            requestModel.Channel,
                            requestModel.Conversation.Id,
                            requestModel.MessageId,
                            MessageTextFormat.Plain,
                            text: null,
                            attachmentList: new List<Attachment> { attachment });
        }

        private async Task<RequestUpdateMessageModel> ProcessCommandRequest(RequestButtonClickedModel requestModel, JObject dataContent)
        {
            Attachment attachment;
            Log.Information($"Employee {requestModel.UserCredentials.Username} requested for information");
            if (dataContent["InputCommand"] == null || String.IsNullOrEmpty(dataContent["InputCommand"]!.ToString().Trim()))
            {
                dataContent["Warning"] = "Указано пустое значение. Пожалуйста, проверьте что поле ввода команды не пустое и повторите поиск.";
                attachment = AdaptiveCardHelper.CreateUpdateAttachment("MainInputTemplate.json", dataContent);
            }
            else
            {
                var connectionString = new SqlConnectionStringBuilder
                {
                    DataSource = "", //paste destination point info here
                    UserID = "", //paste username info here
                    Password = "", //paste password here
                    InitialCatalog = "", //paste DB name here
                    Encrypt = true,
                    TrustServerCertificate = true,
                };
                dataContent["InputCompany"] = Regex.Replace(dataContent["InputCommand"]!.ToString().Trim(), @"\s+", " ");
                Log.Information($"Employee {requestModel.UserCredentials.Username} input data is {dataContent["InputCommand"]!.ToString()}");

                var place = await GetTrainPlace(connectionString, requestModel, dataContent["InputCommand"]!.ToString(), true, dataContent);
                if (place.Rows.Count > 0)
                {
                    Log.Information($"Реестр пассажиров содержит {place.Rows.Count} записей");
                    JArray jPassenger = new JArray();
                    jPassenger = JArray.FromObject(place);
                    JProperty passenger = new JProperty("PassengersList", jPassenger);
                    dataContent.Add(passenger);

                    JArray jPassListToLoad = new JArray();
                    foreach (dynamic item in dataContent["PassengersList"])
                    {
                        JObject jPassToLoad = new JObject();
                        item["lgotType"] = LgotTypeMap(item["lgotType"].ToString());
                        jPassToLoad.Add("title", item["carNumber"] + "-" + item["placeNumber"] + " " + item["passenger_pers"] + " " + item["lgotType"] + " " + item["stationto_str"]);
                        jPassToLoad.Add("value", item["stationFrom"]);
                        jPassListToLoad.Add(jPassToLoad);
                    }
                    JProperty list = new JProperty("ListToLoad", jPassListToLoad);
                    dataContent.Add(list);

                    JObject groupedList = new JObject();

                    var grouped = dataContent["ListToLoad"].GroupBy(
                    item => item["value"].ToString(),
                    item => item["title"].ToString());

                    foreach (var group in grouped)
                    {
                        // Создание JArray для группы по пункту отправления
                        JArray titles = new JArray(group.ToArray());
                        groupedList[group.Key] = titles;
                    }

                    JProperty grouplist = new JProperty("GroupedListToLoad", groupedList);
                    dataContent.Add(grouplist);
                    attachment = AdaptiveCardHelper.CreateUpdateAttachment("InputCommandInfo.json", dataContent);
                }
                else
                {
                    Log.Information($"Нет информации по заданному параметру {dataContent["InputCommand"]!.ToString()}");
                    dataContent["Warning"] = "Нет информации по заданному параметру. Пожалуйста, введите новое значение и повторите поиск.";
                    attachment = AdaptiveCardHelper.CreateUpdateAttachment("MainInputTemplate.json", dataContent);
                }

            }
            return new RequestUpdateMessageModel(
                            requestModel.Channel,
                            requestModel.Conversation.Id,
                            requestModel.MessageId,
                            MessageTextFormat.Plain,
                            text: null,
                            attachmentList: new List<Attachment> { attachment });
        }

        private static async Task<DataTable> GetTrainPlace(SqlConnectionStringBuilder connectionString, RequestButtonClickedModel requestModel, string trainNum, bool site, JObject dataContent)
        {
            DateTime now = DateTime.Now;
            //string formattedDateNow = now.ToString("yyyy-MM-dd");
            string formattedDateNow = "2024-05-01";
            DateTime tomorrow = DateTime.Now.AddDays(1);
            //string formattedDateTomorrow = tomorrow.ToString("yyyy-MM-dd");
            string formattedDateTomorrow = "2024-05-02";

            var queryPlace = File.ReadAllText("/home/wpadmin/SQL/get_fio_place_r2x5.sql");
            var queryRoutList = File.ReadAllText("/home/wpadmin/SQL/get_7000_route_x5.sql");
                using (SqlConnection connection = new SqlConnection(connectionString.ToString()))
                {
                    try {
                        connection.Open();
                        Log.Information($"Employee {requestModel.UserCredentials.Username} successfully connected to database");
                    }
                    catch (Exception ex) {
                        Console.WriteLine(ex.Message);
                        Log.Information($"Employee {requestModel.UserCredentials.Username} failed to connect to database");
                    }
                    var place = new DataTable();
                    using (var command = new SqlCommand(queryPlace, connection))
                    {
                        Log.Information($"Employee {requestModel.UserCredentials.Username} requested passengers list on {trainNum} from {formattedDateNow} to {formattedDateTomorrow}");
                        command.Parameters.Add("@fromTripDate", SqlDbType.DateTime).Value = DateTime.Parse(formattedDateNow);
                        command.Parameters.Add("@tillTripDate", SqlDbType.DateTime).Value = DateTime.Parse(formattedDateTomorrow);
                        command.Parameters.Add("@trainNum", SqlDbType.NVarChar).Value = trainNum;

                        using (var reader = command.ExecuteReader())
                        {
                            place.Load(reader);
                        }

                    }
                    if (place.Rows.Count > 0)
                    {
                        DataTable stRout = new DataTable();
                        stRout.Columns.Add("name", typeof(string));
                        using (var command = new SqlCommand(queryRoutList, connection))
                        {
                            command.Parameters.Add("@tripDate", SqlDbType.DateTime).Value = DateTime.Parse(formattedDateNow);
                            command.Parameters.Add("@trainId", SqlDbType.NVarChar).Value = place.Rows[0]["train_id"];

                            Log.Information($"Значение ID поезда: {place.Rows[0]["train_id"]}");

                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    stRout.Rows.Add(reader["name"].ToString());
                                }
                            }
                        }

                        var uniqueRout = stRout.AsEnumerable().Select(r => r.Field<string>("name")).Distinct().ToList();
                        JArray jRoutes = new JArray();
                        foreach (string item in uniqueRout)
                        {
                            JObject jRoute = new JObject();
                            jRoute.Add("title", item);
                            jRoutes.Add(jRoute);
                        }
                        JProperty route = new JProperty("Route", jRoutes);
                        dataContent.Add(route);

                        foreach (DataRow row in place.Rows)
                        {
                            row["passenger_pers"] = Regex.Replace(row["passenger_pers"].ToString(), @"(\w{3})\w*(\s\w+)", "$1***$2");
                        }

                        place.Columns.Add("stationto_str", typeof(string));

                        foreach (DataRow row in place.Rows)
                        {
                            if (uniqueRout.Contains(row["stationfrom"].ToString()))
                            {
                                if (row["stationto"] != DBNull.Value)
                                {
                                    if (row["stationto"].ToString().Length > 6)
                                    {
                                        row["stationto_str"] = row["stationto"].ToString().Substring(0, 6) + ".";
                                    }
                                    else {
                                        row["stationto_str"] = row["stationto"].ToString() + ".";
                                    }
                                }
                                else {
                                    row["stationto_str"] = "Станция не указана.";
                                }
                            }
                        }

                        if (site)
                        {
                            var result = place.Select("terminaltype = 5").CopyToDataTable();
                            result.DefaultView.Sort = "stationfrom, carnumber, placenumber";
                            return result.DefaultView.ToTable();
                        }
                        else
                        {
                            place.DefaultView.Sort = "stationfrom, carnumber, placenumber";
                            return place.DefaultView.ToTable();
                        }
                    }
                    else
                    {
                        return place;
                    }
                }
        }

        private static string LgotTypeMap(string lgottype)
        {
            return lgottype switch
            {
                "4" => "(В)",
                "5" => "(Ж)",
                "2" => "(Р)",
                "3" => "(У)",
                "1" => "(Ф)",
                "" => "(П)"
            };
        }

        public async Task<ResponseResult> ConversationEndedAsync(ChatBotCommunicator communicator, RequestConversationEndedModel requestModel, CancellationToken cancellationToken)
        {
            await Task.Delay(10);
            Console.WriteLine("Conversation ended");
            return ResponseResult.Ok();
        }

        public Task<ResponseResult> ConversationStartedAsync(ChatBotCommunicator communicator, RequestConversationStartedModel requestModel, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseResult.Ok());
        }

        public Task<ResponseResult> DownloadAttachmentAsync(ChatBotCommunicator communicator, RequestDownloadAttachmentModel requestModel, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseResult.Ok());
        }

        public Task<ResponseResult> MessagesDeliveredAsync(ChatBotCommunicator communicator, RequestMessagesDeliveredModel requestModel, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseResult.Ok());
        }

        public Task<ResponseResult> MessageSeenAsync(ChatBotCommunicator communicator, RequestMessageSeenModel requestModel, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseResult.Ok());
        }

        public async Task<ResponseResult> ReceiveMessageAsync(ChatBotCommunicator communicator, RequestReceiveMessageModel requestModel, IEnumerable<HttpFormData> attachmentFormDataList, CancellationToken cancellationToken)
        {
            var messageForClient = await TryProcessTextAsync(requestModel);
            await communicator.SendMessageAsync(messageForClient, cancellationToken);
            return ResponseResult.Ok();
        }

        private async Task<RequestSendMessageModel> TryProcessTextAsync(RequestReceiveMessageModel requestModel)
        {
            string? text = requestModel.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            DateTime? startDate = null;
            string commandCode = null;
            string textAfterCommand = null;
            if (!(requestModel.TryGetCommandCode(out commandCode, out textAfterCommand)))
            {
                return new RequestSendMessageModel(
                                    requestModel.Channel,
                                    requestModel.Conversation.Id,
                                    MessageTextFormat.Plain,
                                    "Автоматическое определение команды из контекста сообщения не предусмотрено.\nПожалуйста воспользуйтесь меню команд."
                );
            }
            else
            {
                Attachment attachment;
                switch (commandCode)
                {
                    case ChatBotCommand.get_info:
                        attachment = AdaptiveCardHelper.CreateUpdateAttachment("MainInputTemplate.json");
                        return new RequestSendMessageModel(
                                    requestModel.Channel,
                                    requestModel.Conversation.Id,
                                    MessageTextFormat.Plain,
                                    text: null,
                                    attachmentList: new List<Attachment> { attachment });
                        break;
                    default:
                        return new RequestSendMessageModel(
                                    requestModel.Channel,
                                    requestModel.Conversation.Id,
                                    MessageTextFormat.Plain,
                                    "Не удалось обработать команду.");
                        break;
               }
            }
        }

        public Task<ResponseResult> VerifyUserCredentialsAsync(ChatBotCommunicator communicator, RequestVerifyUserCredentialsModel requestModel, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseResult.Ok());
        }
    }
}
