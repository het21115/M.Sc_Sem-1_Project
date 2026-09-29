using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPOInvestmentManagement.Controllers
{
    public class AccountController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly PasswordHasher<User> _passwordHasher;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AccountController> _logger;
        private readonly IWebHostEnvironment _environment;

        public AccountController(
            ApplicationDbContext context,
            IConfiguration configuration,
            ILogger<AccountController> logger,
            IWebHostEnvironment environment)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
            _environment = environment;
            _passwordHasher = new PasswordHasher<User>();
        }

        // =========================
        // REGISTER - GET
        // =========================
        [HttpGet]
        public IActionResult Register()
        {
            return View("~/Views/Home/Register.cshtml");
        }

        // =========================
        // REGISTER - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(User user)
        {
            string requestedRole = user.Role?.Trim() ?? string.Empty;
            if (requestedRole != "Investor" && requestedRole != "Company")
            {
                ModelState.AddModelError("Role", "Please select Investor or Company.");
            }

            if (!ModelState.IsValid)
            {
                return View("~/Views/Home/Register.cshtml", user);
            }

            // Check whether email already exists
            bool emailExists = await _context.Users
                .AnyAsync(u => u.Email == user.Email);

            if (emailExists)
            {
                ModelState.AddModelError(
                    "Email",
                    "This email address is already registered."
                );

                return View("~/Views/Home/Register.cshtml", user);
            }

            string password = user.Password_hash;
            user.Role = "Investor";
            user.Created_at = DateTime.Now;
            user.Is_active = true;
            user.Phone = user.Phone ?? string.Empty;
            user.Password_hash =
                _passwordHasher.HashPassword(user, password);

            string otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            try
            {
                await SendOtpEmailAsync(user.Email, user.First_name, otp);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not send registration OTP to {Email}.", user.Email);
                user.Password_hash = password;
                string emailError = exception.GetBaseException().Message;
                ModelState.AddModelError(
                    "",
                    _environment.IsDevelopment()
                        ? $"OTP email delivery failed: {emailError} Check that SMTP username is the Gmail account that created the App Password, and that the App Password is current."
                        : "We could not send the verification email. Please try again later."
                );

                return View("~/Views/Home/Register.cshtml", user);
            }

            HttpContext.Session.SetString(
                "PendingRegistration",
                JsonSerializer.Serialize(user)
            );
            HttpContext.Session.SetString("RegistrationRequestedRole", requestedRole);
            HttpContext.Session.SetString("RegistrationOtpHash", HashOtp(otp));
            HttpContext.Session.SetString(
                "RegistrationOtpExpires",
                DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString()
            );

            return RedirectToAction("VerifyOtp");
        }

        // =========================
        // VERIFY OTP - GET
        // =========================
        [HttpGet]
        public IActionResult VerifyOtp()
        {
            if (string.IsNullOrWhiteSpace(HttpContext.Session.GetString("PendingRegistration")))
            {
                return RedirectToAction("Register");
            }

            return View("~/Views/Home/VerifyOtp.cshtml");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendOtp()
        {
            string? pendingJson = HttpContext.Session.GetString("PendingRegistration");
            if (pendingJson is null)
            {
                return RedirectToAction("Register");
            }

            User? user = JsonSerializer.Deserialize<User>(pendingJson);
            if (user is null || string.IsNullOrWhiteSpace(user.Email))
            {
                return RedirectToAction("Register");
            }

            string otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            try
            {
                await SendOtpEmailAsync(user.Email, user.First_name, otp);
                HttpContext.Session.SetString("RegistrationOtpHash", HashOtp(otp));
                HttpContext.Session.SetString(
                    "RegistrationOtpExpires",
                    DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString()
                );
                TempData["OtpMessage"] = $"A new verification code was sent to {user.Email}. Check your inbox and Spam folder.";
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not resend registration OTP to {Email}.", user.Email);
                TempData["OtpError"] = "We could not send a new code. Check the SMTP configuration and try again.";
            }

            return RedirectToAction("VerifyOtp");
        }

        // =========================
        // VERIFY OTP - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyOtp(string otp)
        {
            string? pendingJson = HttpContext.Session.GetString("PendingRegistration");
            string? otpHash = HttpContext.Session.GetString("RegistrationOtpHash");
            string? expiresAtValue = HttpContext.Session.GetString("RegistrationOtpExpires");

            if (pendingJson is null || otpHash is null || expiresAtValue is null)
            {
                ModelState.AddModelError("", "Your verification session has expired. Please register again.");
                return View("~/Views/Home/VerifyOtp.cshtml");
            }

            if (!long.TryParse(expiresAtValue, out long expiresAt) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt)
            {
                ModelState.AddModelError("", "This OTP has expired. Please register again.");
                return View("~/Views/Home/VerifyOtp.cshtml");
            }

            if (string.IsNullOrWhiteSpace(otp) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(otpHash),
                    Convert.FromHexString(HashOtp(otp))))
            {
                ModelState.AddModelError("", "The OTP is incorrect. Please try again.");
                return View("~/Views/Home/VerifyOtp.cshtml");
            }

            User? user = JsonSerializer.Deserialize<User>(pendingJson);
            if (user is null)
            {
                ModelState.AddModelError("", "Your registration session is invalid. Please register again.");
                return View("~/Views/Home/VerifyOtp.cshtml");
            }

            bool emailExists = await _context.Users.AnyAsync(u => u.Email == user.Email);
            if (emailExists)
            {
                HttpContext.Session.Remove("PendingRegistration");
                ModelState.AddModelError("", "This email address is already registered.");
                return View("~/Views/Home/VerifyOtp.cshtml");
            }

            bool isCompanyRequest = HttpContext.Session.GetString("RegistrationRequestedRole") == "Company";

            await using var transaction = await _context.Database.BeginTransactionAsync();
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            if (isCompanyRequest)
            {
                await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [Companies]
                    (User_id, Company_name, CIN, Industry, Description, Website, Email, Phone, Address, Approval_status, Created_at)
                    VALUES
                    ({user.User_id}, {user.First_name + " " + user.Last_name + " Company"}, {"PENDING-" + user.User_id}, {string.Empty}, {string.Empty}, {string.Empty}, {user.Email}, {user.Phone}, {string.Empty}, {"Pending"}, {DateTime.Now})
                    """);
            }

            await transaction.CommitAsync();

            if (isCompanyRequest)
            {
                TempData["RegistrationPending"] = "Your Company request was submitted. An Admin must approve it before you can log in.";
            }

            HttpContext.Session.Remove("PendingRegistration");
            HttpContext.Session.Remove("RegistrationRequestedRole");
            HttpContext.Session.Remove("RegistrationOtpHash");
            HttpContext.Session.Remove("RegistrationOtpExpires");
            TempData["RegisteredFirstName"] = user.First_name;

            return RedirectToAction("RegistrationSuccess");
        }

        private async Task SendOtpEmailAsync(string email, string firstName, string otp)
        {
            string host = _configuration["Smtp:Host"] ?? string.Empty;
            int port = _configuration.GetValue<int>("Smtp:Port", 587);
            bool enableSsl = _configuration.GetValue("Smtp:EnableSsl", true);
            string username = _configuration["Smtp:Username"] ?? string.Empty;
            string password = (_configuration["Smtp:Password"] ?? string.Empty).Replace(" ", "");
            string fromEmail = _configuration["Smtp:FromEmail"] ?? string.Empty;
            string fromName = _configuration["Smtp:FromName"] ?? "IPO Investment Management";

            if (string.IsNullOrWhiteSpace(host) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password) ||
                string.IsNullOrWhiteSpace(fromEmail))
            {
                throw new InvalidOperationException("SMTP username, password, and FromEmail must be configured.");
            }

            if (username.Contains("your-email", StringComparison.OrdinalIgnoreCase) ||
                password.Contains("your-email", StringComparison.OrdinalIgnoreCase) ||
                fromEmail.Contains("your-email", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Replace the placeholder SMTP credentials with a real Gmail address and App Password.");
            }

            using var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = "Your IPO Investment Management verification code",
                Body = $"Hello {firstName},\n\nYour verification code is {otp}. It expires in 10 minutes.\n\nIf you did not request this, you can ignore this email.",
                IsBodyHtml = false
            };
            message.To.Add(email);

            using var smtpClient = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(username, password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15000
            };

            await smtpClient.SendMailAsync(message);
        }

        private static string HashOtp(string otp)
        {
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(otp))
            );
        }

        // =========================
        // REGISTRATION SUCCESS - GET
        // =========================
        [HttpGet]
        public IActionResult RegistrationSuccess()
        {
            return View("~/Views/Home/RegistrationSuccess.cshtml");
        }

        // =========================
        // LOGIN - GET
        // =========================
        [HttpGet]
        public IActionResult Login()
        {
            return View("~/Views/Home/Login.cshtml");
        }

        // =========================
        // FORGOT PASSWORD - GET
        // =========================
        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View("~/Views/Home/ForgotPassword.cshtml");
        }

        // =========================
        // FORGOT PASSWORD - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                ModelState.AddModelError("", "Please enter your email address.");
                return View("~/Views/Home/ForgotPassword.cshtml");
            }

            User? user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null)
            {
                TempData["ResetMessage"] = "If an account exists for that email, a verification code has been sent.";
                return RedirectToAction("ForgotPassword");
            }

            string otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            await SendOtpEmailAsync(user.Email, user.First_name, otp);

            HttpContext.Session.SetInt32("PasswordResetUserId", user.User_id);
            HttpContext.Session.SetString("PasswordResetOtpHash", HashOtp(otp));
            HttpContext.Session.SetString(
                "PasswordResetOtpExpires",
                DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString()
            );

            return RedirectToAction("ResetPassword");
        }

        // =========================
        // RESET PASSWORD - GET
        // =========================
        [HttpGet]
        public IActionResult ResetPassword()
        {
            if (!HttpContext.Session.GetInt32("PasswordResetUserId").HasValue)
            {
                return RedirectToAction("ForgotPassword");
            }

            return View("~/Views/Home/ResetPassword.cshtml");
        }

        // =========================
        // RESET PASSWORD - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(
            string otp,
            string newPassword,
            string confirmPassword)
        {
            string? otpHash = HttpContext.Session.GetString("PasswordResetOtpHash");
            string? expiresAtValue = HttpContext.Session.GetString("PasswordResetOtpExpires");
            int? userId = HttpContext.Session.GetInt32("PasswordResetUserId");

            if (!userId.HasValue || otpHash is null || expiresAtValue is null)
            {
                ModelState.AddModelError("", "Your password reset session has expired. Please start again.");
                return View("~/Views/Home/ResetPassword.cshtml");
            }

            if (!long.TryParse(expiresAtValue, out long expiresAt) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt)
            {
                ModelState.AddModelError("", "This OTP has expired. Please request a new one.");
                return View("~/Views/Home/ResetPassword.cshtml");
            }

            if (string.IsNullOrWhiteSpace(otp) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(otpHash),
                    Convert.FromHexString(HashOtp(otp))))
            {
                ModelState.AddModelError("", "The OTP is incorrect.");
            }

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            {
                ModelState.AddModelError("", "The new password must contain at least 6 characters.");
            }

            if (newPassword != confirmPassword)
            {
                ModelState.AddModelError("", "The passwords do not match.");
            }

            if (!ModelState.IsValid)
            {
                return View("~/Views/Home/ResetPassword.cshtml");
            }

            User? user = await _context.Users.FindAsync(userId.Value);
            if (user is null)
            {
                ModelState.AddModelError("", "The account could not be found.");
                return View("~/Views/Home/ResetPassword.cshtml");
            }

            user.Password_hash = _passwordHasher.HashPassword(user, newPassword);
            await _context.SaveChangesAsync();

            HttpContext.Session.Remove("PasswordResetUserId");
            HttpContext.Session.Remove("PasswordResetOtpHash");
            HttpContext.Session.Remove("PasswordResetOtpExpires");
            TempData["SuccessMessage"] = "Your password has been reset. Please sign in.";

            return RedirectToAction("Login");
        }

        // =========================
        // LOGIN - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(
            string Email,
            string Password)
        {
            if (string.IsNullOrWhiteSpace(Email) ||
                string.IsNullOrWhiteSpace(Password))
            {
                ModelState.AddModelError(
                    "",
                    "Email and password are required."
                );

                return View("~/Views/Home/Login.cshtml");
            }

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == Email);

            if (user == null || !user.Is_active)
            {
                ModelState.AddModelError(
                    "",
                    "Invalid email or password."
                );

                return View("~/Views/Home/Login.cshtml");
            }

            var result =
                _passwordHasher.VerifyHashedPassword(
                    user,
                    user.Password_hash,
                    Password
                );

            if (result == PasswordVerificationResult.Failed)
            {
                ModelState.AddModelError(
                    "",
                    "Invalid email or password."
                );

                return View("~/Views/Home/Login.cshtml");
            }

            bool hasPendingCompanyRequest = await _context.Companies.AnyAsync(company =>
                company.User_id == user.User_id &&
                company.Approval_status == "Pending");

            if (hasPendingCompanyRequest)
            {
                ModelState.AddModelError(
                    "",
                    "Your Company request is pending Admin approval. You can log in after it is approved."
                );

                return View("~/Views/Home/Login.cshtml");
            }

            // Store user information in session
            HttpContext.Session.SetInt32(
                "User_id",
                user.User_id
            );

            HttpContext.Session.SetString(
                "UserName",
                $"{user.First_name} {user.Last_name}"
            );

            HttpContext.Session.SetString(
                "UserEmail",
                user.Email
            );

            HttpContext.Session.SetString(
                "UserRole",
                user.Role ?? "User"
            );

            return RedirectToAction("Index", "Dashboard");
        }

        // =========================
        // LOGOUT
        // =========================
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();

            return RedirectToAction("Login");
        }
    }
}