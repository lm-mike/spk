using Bot.Lib.Configuration.Defaults;

namespace Bot.Lib.Configuration
{
    public class DefaultValues
    {
        public string? dataSource {get;set;}
        public string? UID {get;set;}
        public string? Pwd {get;set;}
        public string? DB {get;set;}
        public PassListDefaults passListDefaults { get; set;}
    }
}
