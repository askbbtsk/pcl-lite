using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Net.Http;

public static class HttpSenderExtension
{
    extension(HttpRequestMessage requestMessage)
    {
        public async Task<HttpResponseMessage> SendAsync(
            HttpClient? httpClient = null,
            bool addMetedata = true,
            bool enableLogging = true,
            HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead,
            int retryTimes = 3,
            CancellationToken cancellationToken = default)
        {
            using var request = requestMessage;
            httpClient ??= NetworkService.GetClient();

            if(addMetedata)
            {
                // 只报「我是 PCL Lite」，不带版本号：以前这里和 Referer 里都塞了构建号，
                // 等于每个请求都在向对方上报「谁在用哪个版本」，现在一概不带
                request.WithHeader("User-Agent", "PCL-Lite");
            }

            var requestId = Guid.NewGuid().ToString();
            if (enableLogging)
                LogWrapper.Info(
                    "Request",
                    $"Send request to {request.RequestUri} (method = {request.Method}, id = {requestId})");

            var resp = await NetworkService.GetRetryPolicy(retryTimes)
                .ExecuteAsync(
                    async token =>
                    {
                        if (enableLogging)
                            LogWrapper.Debug("Request", $"Try attempt (id = {requestId})");
                        try
                        {
                            using var requestCopy = await request
                                .CloneAsync()
                                .ConfigureAwait(false);
                            return await httpClient
                                .SendAsync(requestCopy, httpCompletionOption, token)
                                .ConfigureAwait(false);
                        }catch(Exception ex)
                        {
                            // 只在开启日志时记录：抢答式请求会主动取消落后的通道，
                            // 那是预期行为，不该在日志里留下一堆 TaskCanceledException 堆栈。
                            if (enableLogging)
                                LogWrapper.Debug(ex, "Request", $"Try attempt failed (id = {requestId})");
                            throw;
                        }
                    },cancellationToken
                    )
                .ConfigureAwait(false);

            if (enableLogging)
                LogWrapper.Info("Request", $"End request, got http status code {resp.StatusCode} (id = {requestId})");
            return resp;
        }
    }
}
