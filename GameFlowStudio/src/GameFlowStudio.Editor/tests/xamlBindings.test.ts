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

test('普通节点和判断节点属性面板都提供前后台模式选择', () => {
  const mainWindow = readFileSync(
    resolve('..', 'GameFlowStudio.Desktop', 'Views', 'MainWindow.xaml'),
    'utf8',
  )

  assert.equal(
    [...mainWindow.matchAll(/SelectedValue="\{Binding InteractionModeParameter\.Value,/g)].length,
    2,
  )
  assert.equal(
    [...mainWindow.matchAll(/ItemsSource="\{Binding InteractionModeOptions\}"/g)].length,
    2,
  )
})
