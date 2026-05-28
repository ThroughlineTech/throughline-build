using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts;

public interface IReviewFeedbackRetriever
{
    ReviewFeedback? GetLatestRework(string ticketId);
}
