using System;

using DbChronicle.Configuration;

namespace DbChronicle.Embedded
{
    /// <summary>
    /// 组件启动必须条件。核心库不包含数据库驱动实现，执行器在工厂方法上单独注入。
    /// </summary>
    public sealed class DbChronicleOptions
    {
        /// <summary>启动配置文件路径（DbchronicleConfig.xml）；相对路径按宿主程序目录（AppContext.BaseDirectory）解析，与进程当前目录无关。</summary>
        public string ConfigPath { get; set; }

        /// <summary>
        /// 直接提供已构造好的配置（优先于 <see cref="ConfigPath"/>），
        /// 适合宿主在代码里调整方言 / 路径后交给组件。
        /// <see cref="DbChronicleConfig.BaseDirectory"/> 为空时按宿主程序目录补齐。
        /// </summary>
        public DbChronicleConfig Config { get; set; }

        /// <summary>诊断与执行日志输出。</summary>
        public Action<string> Log { get; set; }
    }
}