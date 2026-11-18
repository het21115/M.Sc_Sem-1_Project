using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using IPOInvestmentManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Controllers
{
    [ApiController]
    [Route("api/test-clock")]
    public class IpoTestClockController : ControllerBase
    {
        private readonly IIpoClock _clock;
        private readonly IIpoStatusService _statusService;
        private readonly ApplicationDbContext _context;

        public IpoTestClockController(
            IIpoClock clock,
            IIpoStatusService statusService,
            ApplicationDbContext context)
        {
            _clock = clock;
            _statusService = statusService;
            _context = context;
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok(new
            {
                isSimulated = _clock.IsSimulated,
                currentIstTime = _clock.NowIst.ToString("yyyy-MM-dd HH:mm:ss"),
                currentIstTimeFormatted = _clock.NowIst.ToString("dd MMM yyyy, hh:mm:ss tt"),
                currentUtcTime = _clock.NowUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                simulatedTime = _clock.SimulatedIstTime?.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        [HttpPost("set-time")]
        public async Task<IActionResult> SetSimulatedTime([FromQuery] string time, [FromQuery] bool syncDatabase = true)
        {
            if (!DateTime.TryParse(time, out DateTime parsedTime))
            {
                return BadRequest(new { success = false, message = "Invalid datetime format. Example: 2026-12-07 09:30:00" });
            }

            _clock.SetSimulatedIstTime(parsedTime);

            IpoStatusSyncResult? syncResult = null;
            if (syncDatabase)
            {
                syncResult = await _statusService.SynchronizeIpoStatusesAsync();
            }

            return Ok(new
            {
                success = true,
                message = $"Test clock IST set to: {parsedTime:dd MMM yyyy, hh:mm:ss tt}",
                currentIstTime = _clock.NowIst.ToString("yyyy-MM-dd HH:mm:ss"),
                isSimulated = _clock.IsSimulated,
                syncResult
            });
        }

        [HttpPost("reset-time")]
        public async Task<IActionResult> ResetSimulatedTime([FromQuery] bool syncDatabase = true)
        {
            _clock.ResetSimulatedTime();

            IpoStatusSyncResult? syncResult = null;
            if (syncDatabase)
            {
                syncResult = await _statusService.SynchronizeIpoStatusesAsync();
            }

            return Ok(new
            {
                success = true,
                message = "Test clock reset to real system time.",
                currentIstTime = _clock.NowIst.ToString("dd MMM yyyy, hh:mm:ss tt"),
                isSimulated = _clock.IsSimulated,
                syncResult
            });
        }

        [HttpPost("sync-now")]
        public async Task<IActionResult> SyncNow()
        {
            var result = await _statusService.SynchronizeIpoStatusesAsync();
            return Ok(result);
        }

        [HttpGet("run-all-tests")]
        public async Task<IActionResult> RunAllTests()
        {
            // Create a standardized test IPO matching user specifications:
            // - Vertex Renewable Energy IPO
            // - Opening Date: 7 December 2026, 09:30 AM IST
            // - Opening Date: 7 December 2026, 10:00 AM IST (configured opening time)
            // - Closing Date: 9 December 2026, 05:00 PM IST (configured closing time)
            var sampleIpo = new IPO
            {
                IPO_id = 99999,
                Company_id = 1,
                IPO_name = "Vertex Renewable Energy IPO",
                Open_date = new DateTime(2026, 12, 7),
                Opening_time = new TimeSpan(10, 0, 0),
                Close_date = new DateTime(2026, 12, 9),
                Closing_time = new TimeSpan(17, 0, 0),
                Status = "Approved" // Approved & published by admin
            };

            var rejectedIpo = new IPO
            {
                IPO_id = 99998,
                Company_id = 1,
                IPO_name = "Rejected Sample IPO",
                Open_date = new DateTime(2026, 12, 7),
                Opening_time = new TimeSpan(10, 0, 0),
                Close_date = new DateTime(2026, 12, 9),
                Closing_time = new TimeSpan(17, 0, 0),
                Status = "Rejected"
            };

            var cancelledIpo = new IPO
            {
                IPO_id = 99997,
                Company_id = 1,
                IPO_name = "Cancelled Sample IPO",
                Open_date = new DateTime(2026, 12, 7),
                Opening_time = new TimeSpan(10, 0, 0),
                Close_date = new DateTime(2026, 12, 9),
                Closing_time = new TimeSpan(17, 0, 0),
                Status = "Cancelled"
            };

            var unapprovedIpo = new IPO
            {
                IPO_id = 99996,
                Company_id = 1,
                IPO_name = "Pending Unapproved IPO",
                Open_date = new DateTime(2026, 12, 7),
                Opening_time = new TimeSpan(10, 0, 0),
                Close_date = new DateTime(2026, 12, 9),
                Closing_time = new TimeSpan(17, 0, 0),
                Status = "Pending"
            };

            var testResults = new List<object>();

            // Test 1: 7 December 2026, 09:59 AM IST -> Upcoming
            DateTime t1 = new(2026, 12, 7, 9, 59, 0);
            string s1 = _statusService.CalculateStatus(sampleIpo, t1);
            var (canApply1, reason1) = _statusService.CanApply(sampleIpo, t1);
            testResults.Add(new
            {
                testCase = "1. Before Opening Time (09:59 AM IST)",
                timeIst = t1.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Upcoming",
                actualStatus = s1,
                passed = s1 == "Upcoming" && !canApply1,
                canApply = canApply1,
                applicationValidationReason = reason1
            });

            // Test 2: 7 December 2026, 10:00 AM IST -> Open
            DateTime t2 = new(2026, 12, 7, 10, 0, 0);
            string s2 = _statusService.CalculateStatus(sampleIpo, t2);
            var (canApply2, reason2) = _statusService.CanApply(sampleIpo, t2);
            testResults.Add(new
            {
                testCase = "2. At Exact Opening Time (10:00 AM IST)",
                timeIst = t2.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Open",
                actualStatus = s2,
                passed = s2 == "Open" && canApply2,
                canApply = canApply2,
                applicationValidationReason = reason2
            });

            // Test 3: 7 December 2026, 11:30 AM IST -> Open
            DateTime t3 = new(2026, 12, 7, 11, 30, 0);
            string s3 = _statusService.CalculateStatus(sampleIpo, t3);
            var (canApply3, reason3) = _statusService.CanApply(sampleIpo, t3);
            testResults.Add(new
            {
                testCase = "3. During Open Period (Day 1)",
                timeIst = t3.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Open",
                actualStatus = s3,
                passed = s3 == "Open" && canApply3,
                canApply = canApply3,
                applicationValidationReason = reason3
            });

            // Test 4: 9 December 2026, before closing time (04:59 PM) -> Open
            DateTime t4 = new(2026, 12, 9, 16, 59, 0);
            string s4 = _statusService.CalculateStatus(sampleIpo, t4);
            var (canApply4, reason4) = _statusService.CanApply(sampleIpo, t4);
            testResults.Add(new
            {
                testCase = "4. Final Day Before Closing Time (04:59 PM IST)",
                timeIst = t4.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Open",
                actualStatus = s4,
                passed = s4 == "Open" && canApply4,
                canApply = canApply4,
                applicationValidationReason = reason4
            });

            // Test 5: After closing date and time (9 Dec 2026, 05:01 PM) -> Closed
            DateTime t5 = new(2026, 12, 9, 17, 1, 0);
            string s5 = _statusService.CalculateStatus(sampleIpo, t5);
            var (canApply5, reason5) = _statusService.CanApply(sampleIpo, t5);
            testResults.Add(new
            {
                testCase = "5. After Closing Date and Time (05:01 PM IST)",
                timeIst = t5.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Closed",
                actualStatus = s5,
                passed = s5 == "Closed" && !canApply5,
                canApply = canApply5,
                applicationValidationReason = reason5
            });

            // Test 6: Weekend application restriction (Saturday)
            DateTime tWeekend = new(2026, 12, 12, 12, 0, 0);
            var (canApplyWeekend, reasonWeekend) = _statusService.CanApply(sampleIpo, tWeekend);
            testResults.Add(new
            {
                testCase = "6. Weekend Restriction (Saturday 12:00 PM IST)",
                timeIst = tWeekend.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Closed",
                actualStatus = _statusService.CalculateStatus(sampleIpo, tWeekend),
                passed = !canApplyWeekend,
                canApply = canApplyWeekend,
                applicationValidationReason = reasonWeekend
            });

            // Test 7: After-hours restriction on market day (06:00 PM IST)
            DateTime tAfterHours = new(2026, 12, 7, 18, 0, 0);
            var (canApplyAfterHours, reasonAfterHours) = _statusService.CanApply(sampleIpo, tAfterHours);
            testResults.Add(new
            {
                testCase = "7. After-Hours Restriction (06:00 PM IST)",
                timeIst = tAfterHours.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Open",
                actualStatus = _statusService.CalculateStatus(sampleIpo, tAfterHours),
                passed = !canApplyAfterHours,
                canApply = canApplyAfterHours,
                applicationValidationReason = reasonAfterHours
            });

            // Test 8: Rejected IPO -> Must never become Open (even during open window)
            string s8 = _statusService.CalculateStatus(rejectedIpo, t3);
            var (canApply8, reason8) = _statusService.CanApply(rejectedIpo, t3);
            testResults.Add(new
            {
                testCase = "8. Rejected IPO in Bidding Window",
                timeIst = t3.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Rejected",
                actualStatus = s8,
                passed = s8 == "Rejected" && !canApply8,
                canApply = canApply8,
                applicationValidationReason = reason8
            });

            // Test 9: Cancelled IPO -> Must never become Open (even during open window)
            string s9 = _statusService.CalculateStatus(cancelledIpo, t3);
            var (canApply9, reason9) = _statusService.CanApply(cancelledIpo, t3);
            testResults.Add(new
            {
                testCase = "9. Cancelled IPO in Bidding Window",
                timeIst = t3.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Cancelled",
                actualStatus = s9,
                passed = s9 == "Cancelled" && !canApply9,
                canApply = canApply9,
                applicationValidationReason = reason9
            });

            // Test 10: Unapproved IPO (Pending) -> Must never become Open
            string s10 = _statusService.CalculateStatus(unapprovedIpo, t3);
            var (canApply10, reason10) = _statusService.CanApply(unapprovedIpo, t3);
            testResults.Add(new
            {
                testCase = "10. Unapproved (Pending) IPO in Bidding Window",
                timeIst = t3.ToString("dd MMM yyyy, hh:mm tt"),
                expectedStatus = "Pending",
                actualStatus = s10,
                passed = s10 == "Pending" && !canApply10,
                canApply = canApply10,
                applicationValidationReason = reason10
            });

            // Also test live database IPO #19 (Vertex Renewable Energy IPO)
            var vertexDb = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == 19 || i.IPO_name.Contains("Vertex"));
            object? vertexLiveCheck = null;
            if (vertexDb != null)
            {
                vertexLiveCheck = new
                {
                    ipoId = vertexDb.IPO_id,
                    ipoName = vertexDb.IPO_name,
                    storedDbStatus = vertexDb.Status,
                    openDateTime = _statusService.GetOpeningDateTime(vertexDb).ToString("dd MMM yyyy, hh:mm tt"),
                    closeDateTime = _statusService.GetClosingDateTime(vertexDb).ToString("dd MMM yyyy, hh:mm tt"),
                    currentCalculatedStatus = _statusService.CalculateStatus(vertexDb, _clock.NowIst),
                    at0929am = _statusService.CalculateStatus(vertexDb, t1),
                    at0930am = _statusService.CalculateStatus(vertexDb, t2),
                    at0931am = _statusService.CalculateStatus(vertexDb, t3),
                    beforeClosing = _statusService.CalculateStatus(vertexDb, t4),
                    afterClosing = _statusService.CalculateStatus(vertexDb, t5)
                };
            }

            return Ok(new
            {
                allPassed = testResults.All(r => (bool)r.GetType().GetProperty("passed")!.GetValue(r)!),
                testedAtIst = _clock.NowIst.ToString("dd MMM yyyy, hh:mm:ss tt"),
                testResults,
                vertexLiveDbRecord = vertexLiveCheck
            });
        }
    }
}
