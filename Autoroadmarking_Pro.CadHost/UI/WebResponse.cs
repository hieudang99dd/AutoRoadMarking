namespace Autoroadmarking_Pro.CadHost.UI
{
    public sealed class WebResponse
    {
        public bool Success { get; set; }

        public string Action { get; set; } =
            string.Empty;

        public string Message { get; set; } =
            string.Empty;

        public object? Data { get; set; }

        public static WebResponse Ok(
            string action,
            string message = "",
            object? data = null)
        {
            return new WebResponse
            {
                Success = true,
                Action = action ?? string.Empty,
                Message = message ?? string.Empty,
                Data = data
            };
        }

        public static WebResponse Fail(
            string action,
            string message)
        {
            return new WebResponse
            {
                Success = false,
                Action = action ?? string.Empty,
                Message = message ?? string.Empty,
                Data = null
            };
        }
    }
}
