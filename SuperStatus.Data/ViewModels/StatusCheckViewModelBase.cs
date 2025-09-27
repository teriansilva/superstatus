using Microsoft.Extensions.Primitives;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class StatusCheckViewModelBase
    {
        public StatusCheckViewModelBase() 
        {
            Title = string.Empty;
            StatusCheckUrl = string.Empty;
            WebHookOnErrorUrl = string.Empty;
            ServiceLogoUrl = string.Empty;
        }

        public StatusCheckViewModelBase(StatusCheck statusCheck)
        {
            Id = statusCheck.Id;
            Title = statusCheck.Title;
            StatusCheckUrl = statusCheck.StatusCheckUrl;
            IsWebHookOnErrorEnabled = statusCheck.IsWebHookOnErrorEnabled;
            WebHookOnErrorUrl = statusCheck.WebHookOnErrorUrl;
            ExpectedStatusCode = statusCheck.ExpectedStatusCode;
            ExpectedResponseTimeInMs = statusCheck.ExpectedResponseTimeInMs;
            Description = statusCheck.Description;
            Enabled = statusCheck.Enabled; 
            ServiceLogoUrl = statusCheck.ServiceLogoUrl;
        }

        public long Id { get; set; }
        public string Title { get; set; }
        public string StatusCheckUrl { get; set; }
        public bool IsWebHookOnErrorEnabled { get; set; }
        public int ThrottleWebHookToExecuteOnlyEveryXMinutes { get; set; }
        public string WebHookOnErrorUrl { get; set; }
        public int ExpectedStatusCode { get; set; }
        public long ExpectedResponseTimeInMs { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public string ServiceLogoUrl { get; set; }


    }
}