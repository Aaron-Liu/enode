using System;
using System.Reflection;
using ECommon.Autofac;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.JsonNet;
using ECommon.Log4Net;
using ECommon.Logging;
using ECommon.Utilities;
using ENode.Commanding;
using ENode.Configurations;
using NoteSample.Commands;

namespace NoteSample.QuickStart
{
    class Program
    {
        static ILogger _logger;
        static ENodeConfiguration _configuration;

        static void Main(string[] args)
        {
            InitializeENodeFramework();

            var commandService = ObjectContainer.Resolve<ICommandService>();

            var noteId = ObjectId.GenerateNewStringId();
            var command1 = new CreateNoteCommand { AggregateRootId = noteId, Title = "Sample Title1" };
            var command2 = new ChangeNoteTitleCommand { AggregateRootId = noteId, Title = "Sample Title2" };

            Console.WriteLine(string.Empty);

            commandService.ExecuteAsync(command1, CommandReturnType.EventHandled).Wait();
            commandService.ExecuteAsync(command2, CommandReturnType.EventHandled).Wait();

            Console.WriteLine(string.Empty);

            _logger.Info("Press Enter to exit...");

            Console.ReadLine();
            _configuration.ShutdownEQueue();
        }

        static void InitializeENodeFramework()
        {
            var assemblies = new[]
            {
                Assembly.Load("NoteSample.Domain"),
                Assembly.Load("NoteSample.Commands"),
                Assembly.Load("NoteSample.CommandHandlers"),
                Assembly.GetExecutingAssembly()
            };
            _configuration = Configuration

                // 不可多次实例化，return new Configuration();
                .Create()

                /* 扩展方法，设置容器
                 * ① 设置当前ObjectContainer容器为AutofacObjectContainer
                 * ② AutofacObjectContainer内的属性或使用方法用的属性都是Autofac里面的
                 *    IContainer _container = new ContainerBuilder().Build();
                 *    使用Register更新_container,使用Resolve则去_container里面获取具体类型
                 */
                .UseAutofac()

                /* 注册通用组件 ObjectContainer.Register
                 * <TService, TImplementer>
                 * <ILoggerFactory, EmptyLoggerFactory>
                 * <IBinarySerializer, DefaultBinarySerializer>
                 * <IJsonSerializer, NotImplementedJsonSerializer>
                 * <IScheduleService, ScheduleService>
                 * <IOHelper, IOHelper>
                 */
                .RegisterCommonComponents()

                /* 使用Log4Net记录日志 ObjectContainer.RegisterInstance
                 * <TService, TImplementer>(TImplementer instance)
                 * <ILoggerFactory, Log4NetLoggerFactory>(new Log4NetLoggerFactory("log4net.config"))
                 */
                .UseLog4Net()

                /* 使用JsonNet序列化 ObjectContainer.RegisterInstance
                 * <TService, TImplementer>(TImplementer instance)
                 * <IJsonSerializer, NewtonsoftJsonSerializer>(new NewtonsoftJsonSerializer())
                 */
                .UseJsonNet()

                // 使用Log4Net记录未被捕获的异常
                .RegisterUnhandledExceptionHandler()

                // 使用默认配置文件创建 enode configuration
                .CreateENode()

                /* 注册enode通用组件
                 * <TService, TImplementer>
                 * <ICommandService, NotImplementedCommandService>  此时ICommandService为NotImplementedCommandService
                 */
                .RegisterENodeComponents()
                .RegisterBusinessComponents(assemblies)

                /* 使用代号注册Type，实际上记录在字典里
                 * <Type>(int code)
                 * ①aggregates
                 *   <Note>(1000)
                 * ②commands
                 *   <CreateNoteCommand>(2000)
                 *   <ChangeNoteTitleCommand>(2001)
                 * ③events
                 *   <NoteCreated>(3000)
                 *   <NoteTitleChanged>(3001)
                 * ④event handlers
                 *   <NoteEventHandler>(4000)
                 */
                .RegisterAllTypeCodes()

                /* 此时ICommandService在ObjectContainer内发生变化
                 * TService, TImplementer>(TImplementer instance)
                 * <ICommandService, CommandService>(new CommandService(new CommandResultProcessor(new IPEndPoint(SocketUtils.GetLocalIPV4(), 9000))))
                 */
                .UseEQueue()
                .InitializeBusinessAssemblies(assemblies)
                .StartEQueue();

            Console.WriteLine(string.Empty);

            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(Program).Name);
            _logger.Info("ENode started...");
        }
    }
}
