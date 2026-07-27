using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Automation.Hotkeys;
using GameFlowStudio.Automation.Input;
using GameFlowStudio.Automation.Programs;
using GameFlowStudio.Automation.Windows;
using GameFlowStudio.Automation.Detectors;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Storage.Projects;
using GameFlowStudio.Storage.Settings;
using GameFlowStudio.Workflow.Engine;
using GameFlowStudio.Workflow.Executors;
using GameFlowStudio.Workflow.Validation;
using GameFlowStudio.Workflow.Detectors;
using Microsoft.Extensions.DependencyInjection;

namespace GameFlowStudio.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGameFlowCore(this IServiceCollection services)
    {
        services.AddSingleton<IInputAutomationService, InputAutomationService>();
        services.AddSingleton<IWindowAutomationService, WindowAutomationService>();
        services.AddSingleton<IProgramAutomationService, ProgramAutomationService>();
        services.AddSingleton<IWindowScopeService, WindowScopeService>();
        services.AddSingleton<IDesktopCaptureService, DesktopCaptureService>();
        services.AddSingleton<IStateDetector, ColorStateDetector>();
        services.AddSingleton<IStateDetector, ImageStateDetector>();
        services.AddSingleton<IStateDetector>(new TodoStateDetector(DetectorType.Ocr));
        services.AddSingleton<IStateDetector>(new TodoStateDetector(DetectorType.AiRecognition));
        services.AddSingleton<IStateDetectorService, StateDetectorService>();
        services.AddSingleton<IStateConditionEvaluator, StateConditionEvaluator>();
        services.AddSingleton<IMouseCoordinateResolver, MouseCoordinateResolver>();
        services.AddSingleton<IProjectStorageService, ProjectStorageService>();
        services.AddSingleton<ISettingsStorageService, SettingsStorageService>();
        services.AddSingleton<IWorkflowValidator, WorkflowValidator>();
        services.AddSingleton<CursorPositionService>();
        services.AddSingleton<GlobalHotkeyRegistrar>();
        services.AddSingleton<INodeExecutor, StartNodeExecutor>();
        services.AddSingleton<INodeExecutor, EndNodeExecutor>();
        services.AddSingleton<INodeExecutor, WindowScopeNodeExecutor>();
        services.AddSingleton<INodeExecutor, WaitNodeExecutor>();
        services.AddSingleton<INodeExecutor, MouseMoveNodeExecutor>();
        services.AddSingleton<INodeExecutor, MouseDragNodeExecutor>();
        services.AddSingleton<INodeExecutor, MouseScrollNodeExecutor>();
        services.AddSingleton<INodeExecutor, MouseClickNodeExecutor>();
        services.AddSingleton<INodeExecutor, KeyPressNodeExecutor>();
        services.AddSingleton<INodeExecutor, TextInputNodeExecutor>();
        services.AddSingleton<INodeExecutor>(serviceProvider => new SingleStateConditionNodeExecutor(NodeTypes.ColorCondition, DetectorType.Color, serviceProvider.GetRequiredService<IStateConditionEvaluator>()));
        services.AddSingleton<INodeExecutor>(serviceProvider => new SingleStateConditionNodeExecutor(NodeTypes.ImageCondition, DetectorType.Image, serviceProvider.GetRequiredService<IStateConditionEvaluator>()));
        services.AddSingleton<INodeExecutor, StateConditionNodeExecutor>();
        services.AddSingleton<INodeExecutor, JumpNodeExecutor>();
        services.AddSingleton<INodeExecutor, LogNodeExecutor>();
        services.AddSingleton<INodeExecutor, RunProgramNodeExecutor>();
        services.AddSingleton<INodeExecutor, CloseProgramNodeExecutor>();
        services.AddSingleton<INodeExecutor, ResizeWindowNodeExecutor>();
        services.AddSingleton<INodeExecutor, MinimizeWindowNodeExecutor>();
        services.AddSingleton<INodeExecutor, ArrangeWindowsNodeExecutor>();
        services.AddSingleton<INodeExecutor, SetWindowResolutionNodeExecutor>();
        services.AddSingleton<IWorkflowRunner, WorkflowRunner>();
        return services;
    }
}
