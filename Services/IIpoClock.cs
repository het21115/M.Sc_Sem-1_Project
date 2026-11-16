namespace IPOInvestmentManagement.Services
{
    public interface IIpoClock
    {
        DateTime NowUtc { get; }
        DateTime NowIst { get; }
        TimeZoneInfo IndiaTimeZone { get; }
        DateTime ConvertToIst(DateTime dateTime);
        DateTime ConvertFromUtcToIst(DateTime utcDateTime);
        bool IsSimulated { get; }
        DateTime? SimulatedIstTime { get; }
        void SetSimulatedIstTime(DateTime? simulatedIstTime);
        void ResetSimulatedTime();
    }
}
