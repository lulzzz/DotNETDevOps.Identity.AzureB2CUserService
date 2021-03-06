﻿using Newtonsoft.Json;

namespace DotNETDevOps.Identity.AzureB2CUserService
{
    ///// <summary>
    ///// OData Result
    ///// </summary>
    ///// <typeparam name="T"></typeparam>
    //public class ODataResult<T>
    //{
    //    /// <summary>
    //    /// The odata result
    //    /// </summary>
    //    public T Value { get; set; }


    //}

    public class ODataResult<T>
    {
        [JsonProperty("odata.metadata")]
        public string Metadata { get; set; }
        [JsonProperty("odata.nextLink")]
        public string NextLink { get; set; }
        public T Value { get; set; }
    }

}
