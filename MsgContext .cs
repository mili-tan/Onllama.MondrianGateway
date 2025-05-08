using Microsoft.EntityFrameworkCore;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Onllama.MondrianGateway
{
    internal class MsgContext : DbContext
    {
        public DbSet<MsgEntity> MsgEntities { get; set; }
        public DbSet<RequestMsgIdObj> RequestMsgIdObjs { get; set; }
        public DbSet<RequestHashesObj> RequestHashesObjs { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=msg.db");
            //optionsBuilder.UseMySql(File.Exists("db.text")
            //        ? File.ReadAllText("db.text").Trim()
            //        : Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING"),
            //    new MySqlServerVersion("8.0.0.0"));
        }
    }

    public class MsgEntity
    {
        [Key] [DisplayName("对话 ID")] public string Id { get; set; }
        [DisplayName("对话 Hashes")] public string? Hashes { get; set; }
        [DisplayName("输入内容")] public string? Input { get; set; }
        [DisplayName("输出内容")] public string? Output { get; set; }
        [DisplayName("请求内容")] public string? Body { get; set; }
        [DisplayName("加载")] public long? LoadDuration { get; set; }
        [DisplayName("输入")] public long? PromptDuration { get; set; }
        [DisplayName("输出")] public long? EvalDuration { get; set; }
        [DisplayName("输入 Token")] public int? InputTokens { get; set; }
        [DisplayName("输出 Token")] public int? OutputTokens { get; set; }
        [DisplayName("输出 Token")] public int? TotalTokens { get; set; }

        [DisplayName("创建时间")] public DateTime? Time { get; set; }
        [DisplayName("请求时间")] public long? ReqTime { get; set; }
        [DisplayName("开始时间")] public long? StartTime { get; set; }
        [DisplayName("结束时间")] public long? EndTime { get; set; }
        [DisplayName("结束")] public string? FinishReason { get; set; }
    }

    public class RequestMsgIdObj
    {
        [Key] [DisplayName("对话 ID")] public string Id { get; set; }
        [DisplayName("对话 Hashes")] public string? Hashes { get; set; }

        [DisplayName("会话ID")] public string? SessionId { get; set; }

        [DisplayName("输入内容")] public string? Input { get; set; }
        [DisplayName("创建时间")] public DateTime? Time { get; set; } = DateTime.Now;
    }

    public class RequestHashesObj
    {
        [Key] [DisplayName("对话 Hashes")] public string? Hashes { get; set; }
        [DisplayName("会话ID")] public string? SessionId { get; set; }
        [DisplayName("输入内容")] public string? Input { get; set; }
        [DisplayName("创建时间")] public DateTime? Time { get; set; } = DateTime.Now;
    }
}
