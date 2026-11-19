// Gate no navegador (Fase 22, ADR-0025): o produto é o microsserviço tradutor.
// Dirige o editor real (Tradux.Web) num Edge/Chrome headless pelo
// protocolo DevTools (WebSocket nativo do Node — sem dependências):
//   A. tela inicial → novo template; XSD de origem → XSD de destino; DE-PARA
//      por clique e arraste, "for each" com filtro, concat, tabela; teste;
//      publica a v1 pelos gates; o integrador converte pelo serviço
//      (POST /v1/templates/{nome}/convert); publica a v2; reativa a v1; abre a
//      versão de novo a partir da tela inicial e confere que nada se perdeu.
//   B. segundo template (XML de origem → DPS) e troca do XSD de destino com
//      remapeamento guiado (ADR-0017).
//
// Pré-requisitos: API em http://localhost:5220 (com MongoDB) e editor em
// http://localhost:5230 (dotnet run --launch-profile http em cada projeto).
// Uso: node tests/Tradux.Web.Tests/e2e/golden-path.mjs [--screenshot caminho.png]

import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const EDITOR_URL = process.env.EDITOR_URL ?? 'http://localhost:5230/';
// Credencial de desenvolvimento (appsettings.Development.json guarda só o hash).
const API_KEY = process.env.TRADUX_API_KEY ?? 'tradux_dev-admin-org-a';
const API_URL = process.env.API_URL ?? 'http://localhost:5220';
const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const schemas = (name) => join(repoRoot, 'docs', 'schemas', name);
const colecaoXsdPath = schemas('exemplo-colecao.xsd');
const dpsXsdPath = schemas('exemplo-simples.xsd');
const sourceXsdPath = schemas('origem-exemplo.xsd');
const sourceXml = readFileSync(schemas('origem-exemplo.xml'), 'utf8');
// Mesmo XML de origem com três itens (um de valor 0, que o filtro do "for each" tira).
const itemXml = (descricao, valor) => `<Item><Descricao>${descricao}</Descricao><Quantidade>1</Quantidade><ValorUnitario>${valor}</ValorUnitario></Item>`;
const pedidoXml = sourceXml.replace(/<Itens>[\s\S]*<\/Itens>/, `<Itens>${itemXml('Consultoria', '100')}${itemXml('Cortesia', '0')}${itemXml('Suporte', '50')}</Itens>`);
const screenshotArg = process.argv.indexOf('--screenshot');
const screenshotPath = screenshotArg > 0 ? process.argv[screenshotArg + 1] : null;

const BROWSERS = [
  process.env.BROWSER_PATH,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
].filter(Boolean);

const step = (message) => console.log(`• ${message}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
  const browserPath = BROWSERS.find((p) => existsSync(p));
  if (!browserPath) throw new Error('Nenhum Chrome/Edge encontrado (defina BROWSER_PATH).');

  const profile = mkdtempSync(join(tmpdir(), 'tradux-e2e-'));
  const port = 9300 + Math.floor(Math.random() * 500);
  const browser = spawn(browserPath, [
    '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check', '--window-size=1600,1200', 'about:blank',
  ], { stdio: 'ignore' });

  try {
    const cdp = await connect(port);
    await run(cdp);
    console.log('\nGATE OK: DE-PARA criado, testado e publicado no navegador; o microsserviço converte o XML, versiona, reativa e reabre versões; troca de XSD de destino com remapeamento.');
  } finally {
    browser.kill();
    await sleep(500);
    try { rmSync(profile, { recursive: true, force: true }); } catch { /* perfil em uso */ }
  }
}

// Exceções da página — aparecem também quando uma etapa estoura o tempo.
const pageErrors = [];

/** Integrador: POST /v1/templates/{nome}/convert com o XML de origem. */
async function convert(templateId, xml, query = '') {
  const response = await fetch(`${API_URL}/v1/templates/${templateId}/convert${query}`, {
    method: 'POST', headers: { Authorization: `ApiKey ${API_KEY}`, 'Content-Type': 'application/xml' }, body: xml,
  });
  return { status: response.status, version: response.headers.get('X-Template-Version'), body: await response.text() };
}

// Documento buscado a cada escolha: um redesenho invalida os ids de nó anteriores —
// tenta de novo com o elemento atual.
async function pick(page, selector, file) {
  for (let attempt = 1; ; attempt++) {
    try {
      const { root } = await page.send('DOM.getDocument');
      const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector });
      await page.send('DOM.setFileInputFiles', { nodeId, files: [file] });
      return;
    } catch (e) {
      if (attempt >= 5 || !/node/i.test(e.message)) throw e;
      await sleep(300);
    }
  }
}

async function replaceText(page, selector, text) {
  await mouseClick(page, await center(page, `document.querySelector(${JSON.stringify(selector)})`, selector));
  await page.eval(`document.querySelector(${JSON.stringify(selector)}).select()`);
  await page.send('Input.insertText', { text });
  await sleep(80);
}

async function newTemplate(page, name) {
  await setInput(page, '#new-template-name', name);
  await clickButton(page, 'Criar DE-PARA');
  await waitFor(page, `(document.querySelector('#current-template')?.textContent ?? '').includes(${JSON.stringify(`${name} (novo)`)})`, `editor do template ${name}`);
}

async function loadXsds(page, sourcePath, destinationPath, destinationField) {
  await pick(page, '#source-xsd-files', sourcePath);
  await expectText(page, '#source-schema-panel .file-list', 'origem-exemplo.xsd');
  await clickButton(page, 'Carregar XSD de origem');
  await expectText(page, '#source-schema-status', '<NotaErp>');
  await pick(page, '#xsd-files', destinationPath);
  await clickButton(page, 'Carregar XSD de destino');
  await waitFor(page, `[...document.querySelectorAll('.dest-node .name')].some(n => n.textContent === ${JSON.stringify(destinationField)})`, 'árvore de destino');
}

async function testOk(page, what = 'DE-PARA válido') {
  // Conta as respostas de /v1/design/test: o resultado anterior continua na tela até a nova chegar.
  const before = await page.eval(`(() => {
    if (!window.__testCalls) {
      window.__testCalls = { done: 0 };
      const original = window.fetch;
      window.fetch = async (...args) => {
        const response = await original(...args);
        if (String(args[0]).includes('/v1/design/test')) setTimeout(() => { window.__testCalls.done++; }, 0);
        return response;
      };
    }
    return window.__testCalls.done;
  })()`);
  await clickButton(page, 'Testar');
  await waitFor(page, `window.__testCalls.done > ${before} && !document.querySelector('#btn-test')?.disabled`, 'resposta do teste', 30000);
  await waitFor(page, `!!document.querySelector('#test-panel .banner.ok, #test-panel .banner.error[role="status"]')`, 'resultado do teste', 30000);
  const banner = await page.eval(`document.querySelector('#test-panel [role="status"]')?.textContent ?? ''`);
  if (!banner.includes(what)) {
    const diags = await page.eval(`document.querySelector('#test-panel')?.textContent ?? ''`);
    throw new Error(`Teste não passou: ${banner}\n${diags.slice(0, 3000)}`);
  }
  return page.eval(`document.querySelector('#test-panel pre.xml')?.textContent ?? ''`);
}

async function publish(page, version) {
  await clickButton(page, `Publicar versão ${version}`);
  await waitFor(page, `/Publicado|recusada/.test(document.querySelector('#publish-status')?.textContent ?? '') || document.querySelectorAll('#publish-panel .banner.warning li').length > 0`, 'resultado da publicação', 120000);
  const status = await page.eval(`document.querySelector('#publish-status')?.textContent ?? ''`);
  if (!status.includes(`Publicado: versão ${version}`)) {
    const panel = await page.eval(`document.querySelector('#publish-panel')?.textContent ?? ''`);
    throw new Error(`Publicação da versão ${version} não aceita: ${status}\n${panel.slice(0, 3000)}`);
  }
}

async function run(page) {
  await page.send('Page.enable');
  await page.send('Runtime.enable');
  await page.send('DOM.enable');
  page.on('Runtime.exceptionThrown', (e) => pageErrors.push(e.exceptionDetails?.exception?.description ?? e.exceptionDetails?.text));
  // Diálogos de confirmação bloqueiam o DevTools: aceitos automaticamente.
  page.on('Page.javascriptDialogOpening', () => page.send('Page.handleJavaScriptDialog', { accept: true }));

  step(`abrindo ${EDITOR_URL}`);
  await page.send('Page.navigate', { url: EDITOR_URL });
  await waitFor(page, `document.readyState === 'complete' && !!document.getElementById('home-view')`);
  await page.eval(`localStorage.clear(); sessionStorage.clear()`);
  await page.send('Page.reload');
  await waitFor(page, `!!document.querySelector('#new-template-name')`, 'tela inicial');

  step('0. credencial da API; a tela inicial lista os templates do serviço');
  await setInput(page, '#api-key', API_KEY);
  await clickButton(page, 'Conectar');
  await waitFor(page, `!document.querySelector('#status-bar .banner.error') && !!document.querySelector('#templates-table, #no-templates')`, 'lista de templates com a credencial');
  if (await page.eval(`document.querySelector('#api-key').value`) !== '') throw new Error('O campo de credencial deveria ser limpo depois de usado.');

  const templateA = `pedido-e2e-${Date.now()}`;
  step(`A1. novo template ${templateA}: XSD de origem (NotaErp) → XSD de destino (PedidoComItens)`);
  await newTemplate(page, templateA);
  await loadXsds(page, sourceXsdPath, colecaoXsdPath, 'descricao');
  await expectText(page, '#status-bar', 'obrigatório(s) sem mapeamento');
  await waitFor(page, `[...document.querySelectorAll('.source-field .name')].some(n => n.textContent === '@situacao')`, 'campos de origem vindos do XSD');

  step('A2. numero ← Numero (clique); valorTotal ← Valor (arrastando o conteúdo simples $value)');
  await clickDestination(page, 'numero');
  await clickSource(page, 'Numero');
  await expectSummary(page, 'numero', 'Numero');
  await dragSourceToDestination(page, '$value', 'valorTotal');
  await expectSummary(page, 'valorTotal', '$value');

  step("A2'. XML de exemplo; testando já: o envelope obrigatório <itens> sai vazio (revisão B1)");
  await setInput(page, '#sample-xml', pedidoXml);
  await expectText(page, '#status-bar', 'todos os obrigatórios mapeados');
  const minimal = await testOk(page);
  if (!['<itens />', '<itens/>'].some((t) => minimal.includes(t))) throw new Error(`XML sem <itens/> vazio:\n${minimal}`);

  step('A2". teclado: selecionar um nó com Enter mantém o foco nele (revisão A1)');
  await page.eval(`(${byText('.dest-node .name', 'numero')})?.closest('button').focus()`);
  for (const type of ['keyDown', 'keyUp']) {
    await page.send('Input.dispatchKeyEvent', { type, key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: type === 'keyDown' ? '\r' : undefined });
  }
  await waitFor(page, `document.activeElement?.classList.contains('dest-node') && document.activeElement.querySelector('.name')?.textContent === 'numero' && document.activeElement.classList.contains('selected')`, 'foco continua no nó selecionado');

  step('A3. item: bloco "for each" sobre Itens.Item, com filtro ValorUnitario > 0 no query builder');
  await clickDestination(page, 'item');
  await clickButton(page, 'Criar bloco "for each"');
  await expectSummary(page, 'item', 'para cada item de Itens.Item');
  await clickButton(page, '+ adicionar filtro');
  await clickButton(page, '+ condição');
  await setSelect(page, `[aria-label="Campo da condição"]`, 'ValorUnitario');
  await setSelect(page, `[aria-label="Operador da condição"]`, 'greaterThan');
  await setSelect(page, `[aria-label="Valor da condição: tipo da constante"]`, 'number');
  await setInput(page, `[aria-label="Valor da condição: constante"]`, '0');
  await expectSummary(page, 'item', 'onde ValorUnitario > 0');

  step('A4. campos do item: descricao ← Descricao, valorUnitario ← ValorUnitario (escopo do item)');
  await clickDestination(page, 'descricao');
  await expectText(page, '#source-panel', 'item de Itens.Item');
  await clickSource(page, 'Descricao');
  await clickDestination(page, 'valorUnitario');
  await clickSource(page, 'ValorUnitario');
  await expectText(page, '#status-bar', 'todos os obrigatórios mapeados');

  step('A5. testando o DE-PARA com o XML de exemplo');
  const xml = await testOk(page);
  for (const expected of ['<numero>000123</numero>', '<descricao>Consultoria</descricao>', '<descricao>Suporte</descricao>', '<valorTotal>1500.50</valorTotal>']) {
    if (!xml.includes(expected)) throw new Error(`XML gerado não contém ${expected}:\n${xml}`);
  }
  if (xml.includes('Cortesia')) throw new Error('Filtro do for each não excluiu o item com valor 0.');

  step('A6. transformação: descricao = concat($index, ": ", Descricao), montada no editor de valor');
  await clickDestination(page, 'descricao');
  await setSelect(page, '[aria-label="Valor: tipo"]', 'operator');
  await setSelect(page, '[aria-label="Valor: operador"]', 'concat');
  await setSelect(page, '[aria-label="parte: campo de origem"]', '$index');
  await clickButton(page, '+ parte');
  await setSelect(page, '[aria-label="parte 2: tipo"]', 'literal');
  await setInput(page, '[aria-label="parte 2: constante"]', ': ');
  await clickButton(page, '+ parte');
  await setSelect(page, '[aria-label="parte 3: campo de origem"]', 'Descricao');
  await expectSummary(page, 'descricao', 'concat($index, ": ", Descricao)');
  if (!(await testOk(page)).includes('<descricao>0: Consultoria</descricao>')) throw new Error('Transformação não aplicada no XML gerado.');

  step('A7. obrigatório sem mapeamento: marcação visual + diagnóstico que leva ao campo');
  await clickDestination(page, 'numero');
  await clickButton(page, 'Remover mapeamento');
  await expectText(page, '#status-bar', '1 obrigatório(s) sem mapeamento');
  await waitFor(page, `(${byText('.dest-node .name', 'numero')})?.closest('button').classList.contains('status-missing')`, 'numero marcado como obrigatório sem mapeamento');
  await clickDestination(page, 'valorTotal');
  await clickButton(page, 'Testar');
  await waitFor(page, `(document.querySelector('#test-panel [role="status"]')?.textContent ?? '').includes('ainda não é válido')`, 'teste reprovado');
  await expectText(page, '#test-panel .diagnostics', 'MAPPING_MISSING_REQUIRED');
  await clickButton(page, '/PedidoComItens/numero');
  await waitFor(page, `(${byText('.dest-node .name', 'numero')})?.closest('button').classList.contains('selected')`, 'diagnóstico seleciona o campo');

  step('A8. XML esperado diferente: a diferença é apontada no primeiro clique (revisão A2/A10)');
  await clickDestination(page, 'numero');
  await clickSource(page, 'Numero');
  const expectedXml = '<PedidoComItens xmlns="http://exemplo.ilustrativo.tradux/pedido-colecao/v1" versao="1.00">'
    + '<numero>OUTRO</numero><itens><item><descricao>0: Consultoria</descricao><valorUnitario>100.00</valorUnitario></item>'
    + '<item><descricao>2: Suporte</descricao><valorUnitario>50.00</valorUnitario></item></itens><valorTotal>1500.50</valorTotal></PedidoComItens>';
  await setInput(page, '#expected-xml', expectedXml);
  await clickButton(page, 'Testar');
  await waitFor(page, `(document.querySelector('#test-panel .diagnostics')?.textContent ?? '').includes('EXPECTED_XML_MISMATCH')`, 'diagnóstico de diferença do XML esperado');
  await expectText(page, '#test-panel .diagnostics', '/PedidoComItens/numero');
  await expectText(page, '#test-panel .diagnostics', '"OUTRO"');

  step('A9. tabela DE-PARA: digitar o nome e Enter cria e mostra a tabela na hora (revisão R2)');
  await setInput(page, '[aria-label="Nome da nova tabela DE-PARA"]', 'rotulos');
  for (const type of ['keyDown', 'keyUp']) {
    await page.send('Input.dispatchKeyEvent', { type, key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: type === 'keyDown' ? '\r' : undefined });
  }
  await expectText(page, '#tables-panel', 'rotulos (1)');

  step('A10. testes estruturais automáticos: dados sintéticos do XSD, sem digitar nada');
  await clickButton(page, 'Testes estruturais');
  await waitFor(page, `!!document.querySelector('#quality-panel .banner[role="status"]')`, 'resultado dos testes estruturais');
  const qualityBanner = await page.eval(`document.querySelector('#quality-panel [role="status"]')?.textContent ?? ''`);
  if (!qualityBanner.includes('casos estruturais aprovados')) {
    throw new Error(`Testes estruturais não passaram: ${qualityBanner}\n${await page.eval(`document.querySelector('#quality-panel')?.textContent ?? ''`)}`);
  }
  await expectText(page, '#quality-panel table.quality', 'Repetições');

  step('A11. publicar: XML esperado errado é recusado pelos gates; o correto vira a versão 1');
  await clickButton(page, 'Publicar versão 1');
  await waitFor(page, `(document.querySelector('#publish-status')?.textContent ?? '').includes('recusada')`, 'publicação recusada (cenário com XML esperado errado)');
  await expectText(page, '#publish-panel', 'SCENARIO_FAILED');
  await replaceText(page, '#expected-xml', expectedXml.replace('<numero>OUTRO</numero>', '<numero>000123</numero>'));
  await publish(page, 1);
  await expectText(page, '#versions-table', 'v1');
  await expectText(page, '#current-template', 'a partir da v1');

  step('A12. integrador: POST /v1/templates/{nome}/convert devolve o XML convertido pela v1');
  const v1 = await convert(templateA, pedidoXml);
  if (v1.status !== 200 || v1.version !== '1' || !v1.body.includes('<descricao>0: Consultoria</descricao>')) {
    throw new Error(`Conversão pela v1 falhou (${v1.status}, versão ${v1.version}): ${v1.body.slice(0, 2000)}`);
  }
  const invalid = await convert(templateA, pedidoXml.replace('situacao="1"', 'situacao="9"'));
  if (invalid.status !== 422 || !invalid.body.includes('SOURCE_XML_INVALID')) throw new Error(`XML fora do XSD de origem deveria ser 422: ${invalid.status} ${invalid.body.slice(0, 500)}`);

  step('A13. versão 2: descricao volta a ser só Descricao; o serviço passa a converter com a v2');
  await clickDestination(page, 'descricao');
  await setSelect(page, '[aria-label="Valor: tipo"]', 'field');
  await setSelect(page, '[aria-label="Valor: campo de origem"]', 'Descricao');
  await expectSummary(page, 'descricao', 'Descricao');
  await replaceText(page, '#expected-xml', ' '); // o esperado da v1 não vale mais
  await replaceText(page, '#expected-xml', await testOk(page));
  await publish(page, 2);
  const v2 = await convert(templateA, pedidoXml);
  if (v2.version !== '2' || !v2.body.includes('<descricao>Consultoria</descricao>')) throw new Error(`Conversão pela v2 falhou: ${v2.body.slice(0, 1000)}`);

  step('A14. reativar a v1 pelo histórico: o serviço volta a converter com ela');
  await mouseClick(page, await center(page, `document.querySelector('[aria-label="Reativar a versão 1"]')`, 'reativar v1'));
  await expectText(page, '#status-bar', 'Versão 1 reativada');
  const back = await convert(templateA, pedidoXml);
  if (back.version !== '1' || !back.body.includes('<descricao>0: Consultoria</descricao>')) throw new Error(`Depois do rollback a v1 deveria converter: ${back.body.slice(0, 1000)}`);

  step('A15. tela inicial: o template aparece com a v1 ativa; abrir traz o DE-PARA de volta, sem perder regra');
  await clickButton(page, '← Templates');
  await waitFor(page, `!!document.querySelector('[data-template="${templateA}"]')`, 'template na lista');
  await expectText(page, '#templates-table', 'v1');
  await mouseClick(page, await center(page, `document.querySelector('[data-template="${templateA}"]')`, 'abrir template'));
  await expectText(page, '#current-template', `${templateA} · a partir da v1`);
  await expectSummary(page, 'item', 'onde ValorUnitario > 0');
  await expectSummary(page, 'descricao', 'concat($index, ": ", Descricao)');
  await expectText(page, '#tables-panel', 'rotulos');
  const reopened = await testOk(page, 'e igual ao XML esperado');
  if (!reopened.includes('<descricao>0: Consultoria</descricao>')) throw new Error(`Versão reaberta gera outro XML:\n${reopened}`);

  await partB(page);

  if (screenshotPath) {
    const { data } = await page.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
    writeFileSync(screenshotPath, Buffer.from(data, 'base64'));
    step(`captura de tela: ${screenshotPath}`);
  }

  if (pageErrors.length) throw new Error(`Erros de JavaScript na página:\n${pageErrors.join('\n')}`);
}

// B. XML de origem → DPS e troca do XSD de destino (ADR-0017).
async function partB(page) {
  const templateB = `nota-erp-e2e-${Date.now()}`;
  step(`B1. segundo template ${templateB}: XML de origem (NotaErp) → DPS`);
  await clickButton(page, '← Templates');
  await waitFor(page, `!!document.querySelector('#new-template-name')`, 'tela inicial');
  await newTemplate(page, templateB);
  await loadXsds(page, sourceXsdPath, dpsXsdPath, 'cnpjPrestador');
  await setInput(page, '#sample-xml', sourceXml);

  step('B2. mapeando por clique (caminhos aninhados, atributo, conteúdo simples e nulo)');
  for (const [destination, source] of [
    ['numero', 'Numero'], ['dataEmissao', 'DataEmissao'], ['valorServico', '$value'], ['valorDesconto', 'Desconto'],
    ['codigoServico', 'Codigo'], ['cnpjPrestador', 'Cnpj'], ['codigoSituacao', '@situacao'],
  ]) {
    await clickDestination(page, destination);
    await clickSource(page, source);
  }
  await expectText(page, '#status-bar', 'todos os obrigatórios mapeados');
  const generated = await testOk(page);
  for (const expected of ['>000123<', '>1500.50<', '>12345678000199<', '<codigoSituacao>1</codigoSituacao>']) {
    if (!generated.includes(expected)) throw new Error(`XML gerado do XML de origem não contém ${expected}:\n${generated}`);
  }

  step('B3. publicando a v1 e convertendo pelo serviço: igual ao testado no editor');
  await setInput(page, '#expected-xml', generated);
  await publish(page, 1);
  const converted = await convert(templateB, sourceXml);
  if (converted.status !== 200 || converted.body !== generated) throw new Error(`Conversão pelo serviço difere do teste (${converted.status}):\n${converted.body}\n---\n${generated}`);

  step('B4. XSD de destino v2 (cnpjPrestador renomeado, campo novo obrigatório) sobre o DE-PARA mapeado');
  const dir = mkdtempSync(join(tmpdir(), 'tradux-xsd-v2-'));
  try {
    const v2 = join(dir, 'exemplo-simples.xsd'); // mesmo nome: substitui o arquivo escolhido antes
    writeFileSync(v2, readFileSync(dpsXsdPath, 'utf8')
      .replace('name="cnpjPrestador"', 'name="cnpjDoPrestador"')
      .replace('<xs:element name="numero"', '<xs:element name="codigoMunicipio" type="xs:string"/>\n      <xs:element name="numero"'));
    // Com o XSD já carregado, a troca fica recolhida em "Trocar o XSD de destino".
    await mouseClick(page, await center(page, byText('#schema-panel summary', 'Trocar o XSD de destino (nova versão do layout)'), 'trocar XSD de destino'));
    await pick(page, '#xsd-files', v2);
    await clickButton(page, 'Carregar XSD de destino');
    await expectText(page, '#changes-panel', 'Mapeamentos do XSD anterior para remapear (1)');
    await expectText(page, '#changes-panel', "'codigoMunicipio'");
    await waitFor(page, `!!document.querySelector('.dest-node .change-mark.impact-Map')`, 'campo novo marcado na árvore');

    step('B5. publicar fica bloqueado enquanto houver mapeamento pendente');
    await clickButton(page, 'Publicar versão 2');
    await expectText(page, '#publish-panel', 'remapear ou descartar');

    step('B6. "Mover para" a sugestão: o mapeamento de cnpjPrestador vai para cnpjDoPrestador');
    await mouseClick(page, await center(page,
      `document.querySelector('[aria-label="Mover /DPS/cnpjPrestador para /DPS/cnpjDoPrestador"]')`, 'Mover para a sugestão'));
    await waitFor(page, `!document.querySelector('#orphans')`, 'nenhum mapeamento pendente');
    await expectSummary(page, 'cnpjDoPrestador', 'Cnpj');
    await clickButton(page, 'Fechar relatório');
    await waitFor(page, `!document.querySelector('.schema-changes-panel')`, 'relatório fechado');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

// ---------------------------------------------------------------- ações de usuário

async function center(page, expression, what) {
  const rect = await page.eval(`(() => { const el = ${expression}; if (!el) return null; el.scrollIntoView({ block: 'center' }); const r = el.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  if (!rect) throw new Error(`Elemento não encontrado: ${what}`);
  return rect;
}

async function mouseClick(page, point) {
  for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) {
    await page.send('Input.dispatchMouseEvent', { type, x: point.x, y: point.y, button: 'left', clickCount: 1 });
  }
  await sleep(80);
}

const byText = (selector, text) => `[...document.querySelectorAll(${JSON.stringify(selector)})].find(e => e.textContent.trim() === ${JSON.stringify(text)})`;

const clickButton = async (page, text) => mouseClick(page, await center(page, byText('button', text), `botão "${text}"`));

const clickDestination = async (page, name) =>
  mouseClick(page, await center(page, `${byText('.dest-node .name', name)}?.closest('button')`, `destino ${name}`));

const clickSource = async (page, name) =>
  mouseClick(page, await center(page, `${byText('.source-field .name', name)}?.closest('button')`, `origem ${name}`));

async function dragSourceToDestination(page, sourceName, destinationName) {
  // Arraste HTML5 com DataTransfer real (o headless não gera drag nativo por mouse).
  const ok = await page.eval(`(() => {
    const source = ${byText('.source-field .name', sourceName)}?.closest('button');
    const target = ${byText('.dest-node .name', destinationName)}?.closest('button');
    if (!source || !target) return false;
    const dt = new DataTransfer();
    source.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt }));
    target.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
    target.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
    return true;
  })()`);
  if (!ok) throw new Error(`Arraste ${sourceName} → ${destinationName}: elementos não encontrados.`);
  await sleep(80);
}

async function setSelect(page, selector, value) {
  const ok = await page.eval(`(() => { const s = document.querySelector(${JSON.stringify(selector)}); if (!s || ![...s.options].some(o => o.value === ${JSON.stringify(value)})) return false; s.value = ${JSON.stringify(value)}; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  if (!ok) throw new Error(`Seleção ${selector} = ${value} indisponível.`);
  await sleep(80);
}

async function setInput(page, selector, value) {
  await mouseClick(page, await center(page, `document.querySelector(${JSON.stringify(selector)})`, selector));
  await page.send('Input.insertText', { text: value });
  await sleep(80);
}

async function expectSummary(page, destinationName, text) {
  await waitFor(page, `(${byText('.dest-node .name', destinationName)})?.closest('button').querySelector('.summary').textContent.includes(${JSON.stringify(text)})`,
    `resumo de ${destinationName} contendo "${text}"`);
}

const expectText = (page, selector, text) =>
  waitFor(page, `(document.querySelector(${JSON.stringify(selector)})?.textContent ?? '').includes(${JSON.stringify(text)})`, `"${text}" em ${selector}`);

async function waitFor(page, expression, what = expression, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await page.eval(`(() => { try { return Boolean(${expression}); } catch { return false; } })()`)) return;
    await sleep(100);
  }

  const notice = await page.eval(`[...document.querySelectorAll('#status-bar, #schema-panel .banner, #source-schema-panel .banner, #home-view .banner, #test-panel .banner')].map(e => e.textContent).join(' | ')`);
  const panel = await page.eval(`(() => { const b = [...document.querySelectorAll('button')].find(x => x.textContent.trim() === 'Testar'); const r = b?.getBoundingClientRect(); const hit = r ? document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2) : null; return 'disabled=' + b?.disabled + ' hit=' + (hit?.outerHTML ?? '').slice(0, 120) + ' | ' + (document.querySelector('#test-panel')?.textContent ?? '').slice(0, 120); })()`);
  throw new Error(`Tempo esgotado esperando: ${what}. Barra de status: ${notice}
Painel de teste: ${panel}
Erros: ${pageErrors.join(" / ")}`);
}

// ---------------------------------------------------------------- protocolo DevTools

async function connect(port) {
  let version;
  for (let i = 0; i < 100 && !version; i++) {
    try {
      version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
    } catch {
      await sleep(100);
    }
  }
  if (!version) throw new Error('Navegador headless não respondeu.');

  const socket = new WebSocket(version.webSocketDebuggerUrl);
  await new Promise((res, rej) => { socket.onopen = res; socket.onerror = rej; });

  let nextId = 1;
  const pending = new Map();
  const listeners = new Map();
  let sessionId;

  socket.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve: ok, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? reject(new Error(msg.error.message)) : ok(msg.result);
    } else if (msg.method) {
      listeners.get(msg.method)?.forEach((fn) => fn(msg.params));
    }
  };

  const raw = (method, params = {}, session) => new Promise((ok, reject) => {
    const id = nextId++;
    pending.set(id, { resolve: ok, reject });
    socket.send(JSON.stringify({ id, method, params, ...(session ? { sessionId: session } : {}) }));
  });

  const { targetId } = await raw('Target.createTarget', { url: 'about:blank' });
  ({ sessionId } = await raw('Target.attachToTarget', { targetId, flatten: true }));

  return {
    send: (method, params) => raw(method, params, sessionId),
    on: (method, fn) => listeners.set(method, [...(listeners.get(method) ?? []), fn]),
    async eval(expression) {
      const { result, exceptionDetails } = await raw('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, sessionId);
      if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text);
      return result.value;
    },
  };
}

main().catch((e) => {
  console.error(`\nFALHOU: ${e.message}`);
  process.exit(1);
});
