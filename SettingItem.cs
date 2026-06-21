namespace Emqo.KookBot_Unturned
{
    public class SettingItem
    {
        public string Key { get; set; }
        public bool Value { get; set; }

        public SettingItem()
        {
        }

        public SettingItem(string key, bool value)
        {
            Key = key;
            Value = value;
        }
    }
}
