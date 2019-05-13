using Newtonsoft.Json;
using System;

namespace Br.ESchoolsCalandarToGoogle.Models
{
    public class ESchoolsCalandarEvent
    {
        //Dont be fooled by the Id! its not the 'Event Id' thats why the GoogleId isnt using this Id
        [JsonProperty("id")]
        public string Id { get; set; }

        public string GoogleId => Utilities.Utilities.ConvertToGoogleId($"{Title}-{Start:s}-{End:s}");

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }

        [JsonProperty("start")]
        public DateTime Start { get; set; }

        [JsonProperty("start_obj")]
        public ESchoolsCalandarEventDateObj StartObj { get; set; }

        [JsonProperty("end_obj")]
        public ESchoolsCalandarEventDateObj EndObj { get; set; }

        [JsonProperty("end")]
        public DateTime End { get; set; }

        [JsonProperty("allDay")]
        public bool AllDay { get; set; }
    }


    public class ESchoolsCalandarEventDateObj
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        [JsonProperty("timezone")]
        public string TimeZone { get; set; }
    }
}