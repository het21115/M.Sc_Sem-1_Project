namespace IPOInvestmentManagement.Services
{
    public class IpoClock : IIpoClock
    {
        private static readonly TimeZoneInfo _indiaTimeZone = ResolveIndiaTimeZone();
        private DateTime? _simulatedIstTime;
        private readonly object _lock = new();

        private static TimeZoneInfo ResolveIndiaTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
                }
                catch
                {
                    return TimeZoneInfo.CreateCustomTimeZone("India Standard Time", TimeSpan.FromHours(5.5), "India Standard Time", "India Standard Time");
                }
            }
            catch (InvalidTimeZoneException)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
                }
                catch
                {
                    return TimeZoneInfo.CreateCustomTimeZone("India Standard Time", TimeSpan.FromHours(5.5), "India Standard Time", "India Standard Time");
                }
            }
        }

        public TimeZoneInfo IndiaTimeZone => _indiaTimeZone;

        public DateTime NowIst
        {
            get
            {
                lock (_lock)
                {
                    if (_simulatedIstTime.HasValue)
                    {
                        return _simulatedIstTime.Value;
                    }
                }
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _indiaTimeZone);
            }
        }

        public DateTime NowUtc
        {
            get
            {
                lock (_lock)
                {
                    if (_simulatedIstTime.HasValue)
                    {
                        return TimeZoneInfo.ConvertTimeToUtc(_simulatedIstTime.Value, _indiaTimeZone);
                    }
                }
                return DateTime.UtcNow;
            }
        }

        public bool IsSimulated
        {
            get
            {
                lock (_lock)
                {
                    return _simulatedIstTime.HasValue;
                }
            }
        }

        public DateTime? SimulatedIstTime
        {
            get
            {
                lock (_lock)
                {
                    return _simulatedIstTime;
                }
            }
        }

        public void SetSimulatedIstTime(DateTime? simulatedIstTime)
        {
            lock (_lock)
            {
                _simulatedIstTime = simulatedIstTime;
            }
        }

        public void ResetSimulatedTime()
        {
            lock (_lock)
            {
                _simulatedIstTime = null;
            }
        }

        public DateTime ConvertToIst(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
            {
                return TimeZoneInfo.ConvertTimeFromUtc(dateTime, _indiaTimeZone);
            }

            // Treat Unspecified or Local as representing the Indian calendar date/time
            return dateTime;
        }

        public DateTime ConvertFromUtcToIst(DateTime utcDateTime)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, _indiaTimeZone);
        }
    }
}
