﻿using Microsoft.EntityFrameworkCore;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Onllama.MondrianGateway
{
    internal class MsgContext : DbContext
    {
        public DbSet<MsgThreadEntity> MsgThreadEntities { get; set; }
        public DbSet<MsgRequestIdObj> MsgRequestIdObjs { get; set; }
        public DbSet<RequestHashesObj> RequestHashesObjs { get; set; }
        public DbSet<ProjectsObj> ProjectsObjs { get; set; }
        public DbSet<RiskRuleObj> RiskRuleObjs { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            //optionsBuilder.UseSqlite("Data Source=msg.db");
            optionsBuilder.UseMySql(File.Exists("db.text")
                    ? File.ReadAllText("db.text").Trim()
                    : Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING"),
                new MySqlServerVersion("8.0.0.0"),
                sqloptions =>
                {
                    sqloptions.EnablePrimitiveCollectionsSupport(true);
                });
        }
    }

    public class MsgThreadEntity
    {
        [Key] [DisplayName("对话 ID")] public string Id { get; set; }
        [DisplayName("项目 ID")] public string ProjectId { get; set; } = "default";
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

        [DisplayName("创建时间")] public DateTime? Time { get; set; } = DateTime.UtcNow;
        [DisplayName("请求时间")] public DateTime? ReqTime { get; set; }
        [DisplayName("开始时间")] public DateTime? StartTime { get; set; }
        [DisplayName("结束时间")] public DateTime? EndTime { get; set; }
        [DisplayName("结束")] public string? FinishReason { get; set; }
    }

    public class MsgRequestIdObj
    {
        [Key] [DisplayName("对话 ID")] public string Id { get; set; }
        [DisplayName("项目 ID")] public string ProjectId { get; set; } = "default";
        [DisplayName("项目 ID")] public string ThreadId { get; set; } = "none";
        [DisplayName("对话 Hashes")] public string? Hashes { get; set; }
        [DisplayName("回合 ID")] public string? RoundId { get; set; }
        [DisplayName("请求内容")] public string? Body { get; set; }
        [DisplayName("请求路径")] public string? Path { get; set; }
        [DisplayName("IP")] public string? IP { get; set; }
        [DisplayName("请求方式")] public string? Method { get; set; }
        [DisplayName("请求头")] public string? Header { get; set; }
        [DisplayName("客户端")] public string? UserAgent { get; set; }

        [DisplayName("创建时间")] public DateTime? Time { get; set; } = DateTime.UtcNow;

        [DisplayName("输入内容")] public string? Input { get; set; }
        [DisplayName("输出内容")] public string? Output { get; set; }
        [DisplayName("加载")] public long? LoadDuration { get; set; }
        [DisplayName("输入")] public long? PromptDuration { get; set; }
        [DisplayName("输出")] public long? EvalDuration { get; set; }
        [DisplayName("输入 Token")] public int? InputTokens { get; set; }
        [DisplayName("输出 Token")] public int? OutputTokens { get; set; }
        [DisplayName("输出 Token")] public int? TotalTokens { get; set; }

        [DisplayName("开始时间")] public DateTime? StartTime { get; set; }
        [DisplayName("结束时间")] public DateTime? EndTime { get; set; }
        [DisplayName("结束")] public string? FinishReason { get; set; }
    }

    public class RequestHashesObj
    {
        [Key] [DisplayName("对话 Hashes")] public string? Hashes { get; set; }
        [DisplayName("项目 ID")] public string ProjectId { get; set; } = "default";
        [DisplayName("回合 ID")] public string? RoundId { get; set; }
        [DisplayName("请求内容")] public string? Body { get; set; }
    }

    public class ProjectsObj
    {
        [Key][DisplayName("项目 ID")] public string ProjectId { get; set; } = "default";
        [DisplayName("用户 ID")] public string? UserId { get; set; } = "";
        [DisplayName("密钥")] public string? Keys { get; set; }
        [DisplayName("描述")] public string? Desc { get; set; }
        [DisplayName("创建时间")] public DateTime? Time { get; set; } = DateTime.UtcNow;
        [DisplayName("启用")] public bool Enabled { get; set; } = true;
        [DisplayName("目标 API")] public string TargetApi { get; set; } = "https://127.0.0.1:11434";
    }

    public class RiskRuleObj
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        [DisplayName("项目 ID")] public string ProjectId { get; set; }
        [DisplayName("风险关键性")] public string? RiskKeywords { get; set; }
        [DisplayName("内容安全模型")] public string? RiskModel { get; set; }
        [DisplayName("安全模型提示词")] public string? RiskModelPrompt { get; set; } = string.Empty;
        [DisplayName("创建时间")] public DateTime? Time { get; set; } = DateTime.UtcNow;
        [DisplayName("启用")] public bool Enabled { get; set; } = true;
    }


}
