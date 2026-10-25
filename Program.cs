using System.Threading.Tasks;
using StackExchange.Redis;
Task<RedisValue> t = Task.FromResult(new RedisValue("ban"));
var wrong = t.ToString();
var right = t.Result.ToString();
System.Console.WriteLine($"wrong: {wrong}");
System.Console.WriteLine($"right: {right}");
System.Console.WriteLine($"wrong.Equals(\"ban\"): {wrong.Equals("ban")}");
System.Console.WriteLine($"Task<string>: {Task.FromResult("ban").ToString()}");
