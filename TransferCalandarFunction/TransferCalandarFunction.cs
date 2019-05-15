using System;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Br.ESchoolsCalandarToGoogle.Models;
using System.Collections;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using RestSharp;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace Br.ESchoolsCalandarToGoogle
{

    public static class TransferCalandarFunction
    {
        public static DateTime From = DateTime.Now.Date;
        public static DateTime To = DateTime.Now.AddYears(1).Date;

        static string[] Scopes = { CalendarService.Scope.CalendarEvents };
        static string ApplicationName = "ESchoolsCalandarToGoogle";

        static ILogger Logger;
        static IConfigurationRoot Config;
        static ExecutionContext Context;
        static CalendarService GoogleCalendarService;
        static FinalCount TotalCounts;

        //Run every hour 7am till 7pm (UTC) Monday - Friday
        [FunctionName("TransferCalandarTimerFunction")]
        public static void Run([TimerTrigger("0 0 7-17 * * 1-5")]TimerInfo myTimer, ILogger log, ExecutionContext context)
        {
            Logger = log;
            Config = CreateConfig(context);
            Context = context;
            GoogleCalendarService = CreateGoogleCalendarSerive();
            TotalCounts = new FinalCount();

            log.LogInformation($"TransferCalandarTimerFunction executed at: {DateTime.Now}. From {From} To {To}");

            var eSchoolsEvents = GetEventsFromESchoolsCalandar();
            log.LogInformation($"{eSchoolsEvents.Count} eSchoolsEvents found");
            TotalCounts.TotalESchoolEvents = eSchoolsEvents.Count;

            var googleEvents = GetEventsFromGoogleCalandar();
            log.LogInformation($"{googleEvents.Items.Count} Google Events found");
            TotalCounts.TotalGoogleEvents = googleEvents.Items.Count;

            //Events to Delete
            var deleteEvents = googleEvents.Items.Where(y => y.Status != "cancelled" && !eSchoolsEvents.Any(z => z.GoogleId == y.Id));
            DeleteGoogleEvents(deleteEvents);

            //Events to Update
            var updateEvents = eSchoolsEvents.Where(x => googleEvents.Items.Select(y => y.Id).Contains(x.GoogleId)).ToList();
            UpdateGoogleEvents(updateEvents, googleEvents);

            //Events to Create
            var createEvents = eSchoolsEvents.Where(es => !googleEvents.Items.Any(g => g.Id == es.GoogleId));
            CreateGoogleEvents(createEvents);

            Logger.LogInformation($"Results: {JsonConvert.SerializeObject(TotalCounts)}");
        }

        private static void UpdateGoogleEvents(IEnumerable<ESchoolsCalandarEvent> currentEvents, Events allGoogleEvents)
        {
            foreach (var e in currentEvents)
            {
                var convertedCurrentEvent = CreateGoogleEventFromESchoolsEvent(e);

                Logger.LogInformation($" {convertedCurrentEvent.Summary} ({convertedCurrentEvent.Id})");

                var googleEvent = allGoogleEvents.Items.Where(x => x.Id == convertedCurrentEvent.Id).ToList();

                if (googleEvent == null)
                {
                    Logger.LogInformation($"No Google event found with Id {convertedCurrentEvent.Id}");
                    continue;
                }

                if (googleEvent.Count() > 1)
                {
                    Logger.LogInformation($"Somehow we have a multipal event Ids of {convertedCurrentEvent.Id} how did that happen?");
                    continue;
                }

                if (CheckGoogleEventsAreTheSame(convertedCurrentEvent, googleEvent.First()))
                {
                    Logger.LogInformation($"Event {convertedCurrentEvent.Summary} ({convertedCurrentEvent.Id}) are the same so no action to take");
                    TotalCounts.EventsFoundNotUpdated++;
                }
                else
                {
                    Logger.LogInformation($"Event {convertedCurrentEvent.Summary} ({convertedCurrentEvent.Id}) will be updated");
                    GoogleCalendarService.Events.Update(convertedCurrentEvent, Config["Google:CalendarId"], convertedCurrentEvent.Id).Execute();
                    TotalCounts.EventsFoundAndUpdated++;
                }
            }
        }

        private static bool CheckGoogleEventsAreTheSame(Event one, Event two)
        {
            Type type = typeof(Event);
            List<string> ignoreList = new List<string> {
                "CreatedRaw",
                "Created",
                "Creator",
                "ETag",
                "HtmlLink",
                "ICalUID",
                "Kind",
                "Organizer",
                "Reminders",
                "Sequence",
                "UpdatedRaw",
                "Updated",
                "OriginalStartTime"
            };

            bool eventsAreSame = true;

            foreach (System.Reflection.PropertyInfo pi in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!ignoreList.Contains(pi.Name))
                {
                    object oneValue = type.GetProperty(pi.Name).GetValue(one, null);
                    object twoValue = type.GetProperty(pi.Name).GetValue(two, null);

                    //this is a bit rubbish but will do until i think of a better way
                    //TODO: must be a better way to do this
                    if (pi.PropertyType == typeof(EventDateTime))
                    {
                        EventDateTime oneEventDateTime = (EventDateTime)oneValue;
                        EventDateTime twoEventDateTime = (EventDateTime)twoValue;

                        if (oneEventDateTime.TimeZone == null)
                            oneEventDateTime.TimeZone = string.Empty;

                        if (twoEventDateTime.TimeZone == null)
                            twoEventDateTime.TimeZone = string.Empty;

                        if (!string.Equals(oneEventDateTime.Date, twoEventDateTime.Date) ||
                            !DateTime.Equals(oneEventDateTime.DateTime, twoEventDateTime.DateTime) ||
                            !string.Equals(oneEventDateTime.TimeZone, twoEventDateTime.TimeZone))
                        {
                            eventsAreSame = false;
                        }
                        continue;
                    }
                    //end of rubbish bit

                    if (oneValue != twoValue && (oneValue == null || !oneValue.Equals(twoValue)))
                    {
                        if ((oneValue == null || string.IsNullOrEmpty(oneValue.ToString())) && (twoValue == null || string.IsNullOrEmpty(twoValue.ToString())))
                            continue;

                        Logger.LogInformation($"Property {pi.Name} differ value one '{oneValue}' value '{twoValue}'");
                        eventsAreSame = false;
                    }
                }
            }
            return eventsAreSame;
        }

        private static void DeleteGoogleEvents(IEnumerable<Event> events)
        {
            foreach (var e in events)
            {
                Logger.LogInformation($"Deleteing event {e.Summary} ({e.Id})");
                GoogleCalendarService.Events.Delete(Config["Google:CalendarId"], e.Id).Execute();
                TotalCounts.EventsDeleted++;
            }
        }

        private static void CreateGoogleEvents(IEnumerable<ESchoolsCalandarEvent> events)
        {
            foreach (var e in events)
            {
                var newEvent = CreateGoogleEventFromESchoolsEvent(e);

                Logger.LogInformation($"Creating new event {newEvent.Summary} ({newEvent.Id})");
                GoogleCalendarService.Events.Insert(newEvent, Config["Google:CalendarId"]).Execute();
                TotalCounts.EventsDeleted++;
            }
        }
        private static Event CreateGoogleEventFromESchoolsEvent(ESchoolsCalandarEvent eschoolEvent)
        {
            int daysDiff = ((TimeSpan)(eschoolEvent.EndObj.Date - eschoolEvent.StartObj.Date)).Days;

            var googleEvent = new Event()
            {
                Id = eschoolEvent.GoogleId,

                Start = new EventDateTime
                {
                    DateTime = !eschoolEvent.AllDay ? eschoolEvent.StartObj.Date : (DateTime?)null,
                    Date = eschoolEvent.AllDay ? eschoolEvent.StartObj.Date.ToString("yyyy-MM-dd") : null,
                    TimeZone = !eschoolEvent.AllDay ? eschoolEvent.StartObj.TimeZone : null

                },
                End = new EventDateTime
                {
                    DateTime = !eschoolEvent.AllDay ? eschoolEvent.EndObj.Date : (DateTime?)null,
                    Date = eschoolEvent.AllDay ? daysDiff > 1 ? eschoolEvent.EndObj.Date.AddDays(1).ToString("yyyy-MM-dd") : eschoolEvent.EndObj.Date.ToString("yyyy-MM-dd") : null,
                    TimeZone = !eschoolEvent.AllDay ? eschoolEvent.EndObj.TimeZone : null
                },
                Summary = eschoolEvent.Title,
                Description = eschoolEvent.Details,
                Location = eschoolEvent.Location,
                Status = "confirmed"
            };

            return googleEvent;
        }

        private static IReadOnlyList<ESchoolsCalandarEvent> GetEventsFromESchoolsCalandar()
        {
            var client = new RestClient(Config["ESchools:BaseAddress"]);
            var request = new RestRequest(Method.POST);
            var body = $"start={GetEpocDate(From)}&end={GetEpocDate(To)}";
            var formData = Encoding.UTF8.GetBytes(body);
            request.AddParameter("application/x-www-form-urlencoded", formData, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);

            var formatted = JsonConvert.DeserializeObject<List<ESchoolsCalandarEvent>>(response.Content);

            return formatted;
        }
        private static Events GetEventsFromGoogleCalandar()
        {
            // Define parameters of request.
            EventsResource.ListRequest request = GoogleCalendarService.Events.List(Config["Google:CalendarId"]);
            request.ShowDeleted = false;
            request.SingleEvents = true;
            request.MaxResults = 2500;
            request.ShowDeleted = true;
            request.TimeMin = From.AddDays(-1);
            request.TimeMax = To.AddDays(1);

            // List events.
            Events events = request.Execute();

            return events;
        }

        private static CalendarService CreateGoogleCalendarSerive()
        {
            UserCredential credential;
            var basePath = Path.Combine(Context.FunctionAppDirectory, "Files");

            Logger.LogInformation($"Files base path: {basePath}");

            using (var stream =
                            new FileStream(Path.Combine(basePath, "credentials.json"), FileMode.Open, FileAccess.Read))
            {
                // The file token.json stores the user's access and refresh tokens, and is created
                // automatically when the authorization flow completes for the first time.
                string credPath = Path.Combine(basePath, "token.json");
                credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    Scopes,
                    "user",
                 System.Threading.CancellationToken.None,
                    new FileDataStore(credPath, true)).Result;
                Console.WriteLine("Credential file saved to: " + credPath);
            }

            // Create Google Calendar API service.
            var service = new CalendarService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            return service;
        }
        private static int GetEpocDate(DateTime dateTime)
        {
            TimeSpan t = dateTime - new DateTime(1970, 1, 1);
            return (int)t.TotalSeconds;
        }

        private static IConfigurationRoot CreateConfig(ExecutionContext context)
        {
            var configBuilder = new ConfigurationBuilder()
                        .SetBasePath(context.FunctionAppDirectory)
                        .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                        .AddEnvironmentVariables();

            var envrio = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", EnvironmentVariableTarget.Process);

            if (envrio == "Development")
            {
                configBuilder.AddUserSecrets("03dd2206-ac2a-488d-aa78-bc323ca8a623");
            }

            return configBuilder.Build();
        }
    }
}
