﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MiniWebServer.Abstractions.Http
{
    public class HttpRequestHeaders: HttpHeaders
    {
        public string Host { 
            get
            {
                return TryGetValueAsString("Host");
            }
        }
        public string Connection
        {
            get
            {
                return TryGetValueAsString("Connection");
            }
        }

        private string TryGetValueAsString(string name, string defaultValue = "")
        {
            if (TryGetValue(name, out var value))
            {
                if (value == null)
                    return defaultValue;

                return value.Value.FirstOrDefault(defaultValue);
            }
            else
            {
                return defaultValue;
            }

        }
    }
}
