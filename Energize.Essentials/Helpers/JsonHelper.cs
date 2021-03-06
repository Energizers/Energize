﻿using Newtonsoft.Json;
using System;

namespace Energize.Essentials.Helpers
{
    public class JsonHelper
    {
        public static bool TryDeserialize<T>(string json, Logger logger, out T value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    value = default;
                    return false;
                }

                value = JsonConvert.DeserializeObject<T>(json);
                return true;
            }
            catch (Exception ex)
            {
                logger.Nice("JSON", ConsoleColor.Red, ex.Message);
                value = default;
                return false;
            }
        }

        public static bool TrySerialize(object obj, Logger logger, out string json)
        {
            try
            {
                json = JsonConvert.SerializeObject(obj);
                return true;
            }
            catch (Exception ex)
            {
                logger.Nice("JSON", ConsoleColor.Red, ex.Message);
                json = string.Empty;
                return false;
            }
        }

    }
}
