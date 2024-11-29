namespace Bot.Models
{
    public class User
    {
            public int Id { get; set; }
    public string Username { get; set; }
    public string Displayname { get; set; }
    public int Seatnumber { get; set; }
    }

    public class ResponseModel : Dictionary<string, List<User>>
{

}
}
