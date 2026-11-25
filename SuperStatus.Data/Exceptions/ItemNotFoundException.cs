namespace SuperStatus.Data.Exceptions;

/// <summary>
/// Exception thrown when an item is not found.
/// </summary>
public class ItemNotFoundException : Exception
{
    public ItemNotFoundException()
        : base("The item was not found.")
    {
    }

    public ItemNotFoundException(string itemName)
        : base($"{itemName} was not found.")
    {
    }

    public ItemNotFoundException(string itemName, int itemId)
        : base($"{itemName} with Id {itemId} was not found.")
    {
    }

    public ItemNotFoundException(string itemName, string itemId)
        : base($"{itemName} with Id {itemId} was not found.")
    {
    }

    public ItemNotFoundException(string itemName, int itemId, string parentName, int parentId)
        : base($"{itemName} with Id {itemId} was not found under parent {parentName} with Id {parentId}.")
    {
    }

    public ItemNotFoundException(string message, Exception inner)
        : base(message, inner)
    {
    }
}