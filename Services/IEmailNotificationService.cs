namespace IPOInvestmentManagement.Services
{
    public interface IEmailNotificationService
    {
        Task SendAsync(string recipientEmail, string recipientName, string subject, string body);
    }
}
