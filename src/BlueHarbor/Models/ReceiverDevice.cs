namespace BlueHarbor.Models;

public sealed record ReceiverDevice(string Id, string Name, bool IsEnabled)
{
    public string ShortAddress
    {
        get
        {
            var index = Id.LastIndexOf('#');
            if (index < 0 || index + 1 >= Id.Length)
            {
                return string.Empty;
            }

            var tail = Id[(index + 1)..];
            var underscore = tail.IndexOf('_');
            return underscore > 0 ? tail[..underscore] : tail;
        }
    }
}
