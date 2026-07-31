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

test('脚本属性只提供窗口优先和动作优先', () => {
  const desktopDirectory = resolve('..', 'GameFlowStudio.Desktop')
  const mainWindow = readFileSync(resolve(desktopDirectory, 'Views', 'MainWindow.xaml'), 'utf8')
  const helpWindow = readFileSync(resolve(desktopDirectory, 'Views', 'HelpWindow.xaml'), 'utf8')
  const mainViewModel = readFileSync(resolve(desktopDirectory, 'ViewModels', 'MainViewModel.cs'), 'utf8')
  const windowModeUi = `${mainWindow}\n${helpWindow}\n${mainViewModel}`

  assert.doesNotMatch(windowModeUi, /WindowExecutionMode\.FirstOnly|只操作第一个窗口/)
  assert.match(mainViewModel, /_scriptWindowExecutionMode = WindowExecutionMode\.WindowPriority/)
  assert.equal(
    [...mainViewModel.matchAll(/new\(WindowExecutionMode\.(?:WindowPriority|ActionPriority),/g)].length,
    2,
  )
  assert.equal(
    [...mainWindow.matchAll(/ItemsSource="\{Binding WindowExecutionModes\}"/g)].length,
    1,
  )
})
