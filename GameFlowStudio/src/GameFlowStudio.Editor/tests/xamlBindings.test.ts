import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'
import test from 'node:test'

test('可选择只读文本的动态 Text 绑定必须显式使用 OneWay', () => {
  const viewsDirectory = resolve('..', 'GameFlowStudio.Desktop', 'Views')
  const selectableBinding = /<controls:SelectableTextBlock\b[^>]*\bText="\{Binding[^"]*}"[^>]*\/?>/g

  for (const fileName of readdirSync(viewsDirectory).filter(name => name.endsWith('.xaml'))) {
    const xaml = readFileSync(resolve(viewsDirectory, fileName), 'utf8')
    for (const tag of xaml.matchAll(selectableBinding)) {
      assert.match(
        tag[0],
        /\bMode\s*=\s*OneWay\b/,
        `${fileName} 中 SelectableTextBlock 的动态 Text 绑定必须显式设置 Mode=OneWay：${tag[0]}`,
      )
    }
  }
})

test('前后台模式只在脚本属性中提供且不显示提示', () => {
  const mainWindow = readFileSync(
    resolve('..', 'GameFlowStudio.Desktop', 'Views', 'MainWindow.xaml'),
    'utf8',
  )

  assert.equal(
    [...mainWindow.matchAll(/SelectedValue="\{Binding InteractionModeParameter\.Value,/g)].length,
    0,
  )
  assert.equal(
    [...mainWindow.matchAll(/ItemsSource="\{Binding InteractionModeOptions\}"/g)].length,
    0,
  )
  assert.equal(
    [...mainWindow.matchAll(/ItemsSource="\{Binding ScriptInteractionModes\}"/g)].length,
    1,
  )
  assert.equal(
    [...mainWindow.matchAll(/SelectedValue="\{Binding ScriptInteractionMode, Mode=TwoWay\}"/g)].length,
    1,
  )
  assert.doesNotMatch(mainWindow, /InteractionModeHint/)
})

test('脚本属性按前后台模式提供对应的操作流程', () => {
  const desktopDirectory = resolve('..', 'GameFlowStudio.Desktop')
  const mainWindow = readFileSync(resolve(desktopDirectory, 'Views', 'MainWindow.xaml'), 'utf8')
  const helpWindow = readFileSync(resolve(desktopDirectory, 'Views', 'HelpWindow.xaml'), 'utf8')
  const mainViewModel = readFileSync(resolve(desktopDirectory, 'ViewModels', 'MainViewModel.cs'), 'utf8')
  const operationFlowUi = `${mainWindow}\n${helpWindow}\n${mainViewModel}`

  assert.match(mainWindow, /Text="操作流程"/)
  assert.doesNotMatch(operationFlowUi, /前台串行方式|窗口优先|动作优先/)
  assert.match(mainViewModel, /_scriptOperationFlow = WindowExecutionMode\.Serial/)
  assert.equal(
    [...mainViewModel.matchAll(/new\(WindowExecutionMode\.(?:PseudoParallel|Serial|Parallel),/g)].length,
    3,
  )
  assert.match(mainViewModel, /new\(WindowExecutionMode\.PseudoParallel, "伪并行"/)
  assert.match(mainViewModel, /new\(WindowExecutionMode\.Serial, "串行"/)
  assert.match(mainViewModel, /new\(WindowExecutionMode\.Parallel, "并行"/)
  assert.match(
    mainViewModel,
    /ScriptInteractionMode == ScriptInteractionModeValue\.Background\s*\? BackgroundOperationFlowModes\s*:\s*ForegroundOperationFlowModes/,
  )
  assert.equal(
    [...mainWindow.matchAll(/ItemsSource="\{Binding OperationFlowModes\}"/g)].length,
    1,
  )
  assert.equal(
    [...mainWindow.matchAll(/SelectedValue="\{Binding ScriptOperationFlow, Mode=TwoWay\}"/g)].length,
    1,
  )
  assert.match(helpWindow, /串行（前台）/)
  assert.match(helpWindow, /伪并行（前台）/)
  assert.match(helpWindow, /并行（后台）/)
})

test('脚本匹配模式先包含窗口再排除不需要的目标', () => {
  const desktopDirectory = resolve('..', 'GameFlowStudio.Desktop')
  const mainWindow = readFileSync(resolve(desktopDirectory, 'Views', 'MainWindow.xaml'), 'utf8')
  const helpWindow = readFileSync(resolve(desktopDirectory, 'Views', 'HelpWindow.xaml'), 'utf8')
  const mainViewModel = readFileSync(resolve(desktopDirectory, 'ViewModels', 'MainViewModel.cs'), 'utf8')
  const scriptMatchingUi = `${mainWindow}\n${mainViewModel}`

  assert.match(mainWindow, /Text="匹配模式"/)
  assert.match(mainWindow, /Content="整个屏幕"/)
  assert.match(mainWindow, /Content="正则匹配窗口"/)
  assert.match(
    mainWindow,
    /Visibility="\{Binding ScriptWindowScopeEnabled, Converter=\{StaticResource BoolVisibility\}\}"/,
  )
  assert.equal(
    [...mainWindow.matchAll(/Text="\{Binding ScriptWindowMatchPattern, UpdateSourceTrigger=PropertyChanged\}"/g)].length,
    1,
  )
  assert.equal(
    [...mainWindow.matchAll(/Text="\{Binding ScriptWindowExcludePattern, UpdateSourceTrigger=PropertyChanged\}"/g)].length,
    1,
  )
  assert.match(mainWindow, /Text="规定要操作哪些窗口"/)
  assert.match(mainWindow, /Text="从正则匹配结果中排除不需要的匹配目标，留空表示不排除"/)
  assert.match(mainViewModel, /ActiveScript\.WindowScope\.MatchPattern = value/)
  assert.match(mainViewModel, /ActiveScript\.WindowScope\.ExcludePattern = value/)
  assert.doesNotMatch(scriptMatchingUi, /ScriptWindowTitlePattern|Text="窗口范围"|窗口标题正则（正则匹配窗口）/)
  assert.match(helpWindow, /先使用“正则匹配”/)
  assert.match(helpWindow, /再使用“排除正则匹配”/)
  assert.match(helpWindow, /全部被排除时运行失败/)
  assert.match(helpWindow, /包含与排除规则都须一致/)
  assert.match(helpWindow, /不能重新加入被脚本排除的目标/)
})
