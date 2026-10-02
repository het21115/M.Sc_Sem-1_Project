using System.Net;
using System.Net.Mail;

namespace IPOInvestmentManagement.Services
{
    public class SmtpEmailNotificationService : IEmailNotificationService
    {
        private readonly IConfiguration _configuration;

        public SmtpEmailNotificationService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task SendAsync(string recipientEmail, string recipientName, string subject, string body)
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
                throw new InvalidOperationException("SMTP host, username, password, and FromEmail must be configured.");
            }

            using var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = $"Hello {recipientName},\n\n{body}\n\nIPO Investment Management",
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(recipientEmail, recipientName));

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
    }
}
