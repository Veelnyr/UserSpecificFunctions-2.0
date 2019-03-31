using System.IO;
using Newtonsoft.Json;

namespace UserSpecificFunctions
{
    public sealed class UserSpecificFunctionsConfig
    {
        public double CapsRatio = 0.6;
        public double CapsWeight = 2.0;
        public double NormalWeight = 1.0;
        public int ShortLength = 4;
        public double ShortWeight = 1.5;
        public double RepeatMsgWeight = 4.0;
        public double CommandWeight = 1.0;
        public double Threshold = 5.0;
        public double KickThreshold = 11.0;
        public int Time = 5;
        public string SpamWarningMsg = "You have been ignored for spamming / Вы пишете слишком часто";
        public string SpamKickReason = "Spamming / Спам";

        public int MaximumPrefixLength { get; } = 100;
        public int MaximumSuffixLength { get; } = 100;
        
        public static UserSpecificFunctionsConfig ReadOrCreate(string configPath)
        {
            if (File.Exists(configPath))
            {
                return JsonConvert.DeserializeObject<UserSpecificFunctionsConfig>(File.ReadAllText(configPath));
            }

            var config = new UserSpecificFunctionsConfig();
            File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
            return config;
        }
    }
}