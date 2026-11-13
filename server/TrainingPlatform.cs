using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

public static class TrainingPlatform
{
    // These helpers reuse the caller's catalog/intake transaction and never initialize tables.
    public static TrainingModelRef Freeze(
        SqliteConnection c,
        SqliteTransaction tx,
        TrainingModelRef reference
    ) => TrainingStore.Freeze(c, tx, reference);

    public static string? Validate(TrainingModelRef? reference)
    {
        if (reference is null)
            return null;
        try
        {
            TrainingStore.Resolve(reference);
            return null;
        }
        catch (WorkflowException ex)
        {
            return ex.Message;
        }
    }
}
