// Teste do DE-PARA (seção 12: "testar com XML de exemplo", "comparar o XML
// gerado", "visualizar erros de tipo e cardinalidade").

import { h } from './dom.js';
import { xpathToPath, testDiagnostics } from '../model/diagnostics.js';
import { decodeXml } from '../model/files.js';

const SEVERITY_LABEL = { Critical: 'crítico', Warning: 'aviso', Info: 'informação' };

/**
 * Entradas do teste e os botões. props: { sampleXml, expectedXml, busy, stale,
 *   onSampleXml(text), onExpectedXml(text), onTest(), onQuality() }
 */
export function testControls({ sampleXml, expectedXml, busy, stale, onSampleXml, onExpectedXml, onTest, onQuality }) {
  return h('div', { class: 'test-controls' },
    h('label', { for: 'sample-xml' }, 'XML de exemplo (no formato do XSD de origem)'),
    h('textarea', {
      id: 'sample-xml', rows: 6, spellcheck: 'false', value: sampleXml,
      placeholder: '<?xml version="1.0"?>\n<NotaErp ...>...</NotaErp>',
      oninput: (e) => onSampleXml(e.target.value),
    }),
    h('input', {
      type: 'file', accept: '.xml,application/xml,text/xml', 'aria-label': 'Carregar XML de exemplo de arquivo',
      onchange: async (e) => {
        const file = e.target.files[0];
        if (file) onSampleXml(decodeXml(await file.arrayBuffer()));
      },
    }),
    h('label', { for: 'expected-xml' }, 'XML esperado (comparação semântica; obrigatório para publicar)'),
    h('textarea', {
      id: 'expected-xml', rows: 4, spellcheck: 'false', value: expectedXml,
      oninput: (e) => onExpectedXml(e.target.value),
    }),
    h('button', { type: 'button', class: 'primary', id: 'btn-test', disabled: busy, onclick: onTest }, 'Testar'),
    ' ',
    h('button', { type: 'button', id: 'run-quality', disabled: busy, onclick: onQuality, title: 'Gera dados sintéticos do XSD e testa obrigatórios, limites, enumerações, repetições, nulos e inválidos' }, 'Testes estruturais'),
    stale && h('span', { class: 'muted' }, ' O DE-PARA mudou desde o último teste.'));
}

/**
 * Resultado do teste. props: { result (DraftTestResult|null), localProblems: string[], error: string|null,
 *   mappingTemplate, onSelectPath(path), busy }
 */
export function testPanel({ result, localProblems, error, mappingTemplate, onSelectPath, busy }) {
  const parts = [];

  if (busy) parts.push(h('p', { class: 'muted' }, 'Testando…'));
  if (error) parts.push(h('p', { class: 'banner error', role: 'alert' }, error));

  if (localProblems.length > 0) {
    parts.push(h('div', { class: 'banner warning', role: 'alert' },
      h('strong', {}, 'Complete o DE-PARA antes de testar:'),
      h('ul', {}, localProblems.map((p) => h('li', {}, p)))));
  }

  if (result) {
    const generation = result.generation;
    const diagnostics = testDiagnostics(result);

    parts.push(h('div', { class: `banner ${result.publishable ? 'ok' : 'error'}`, role: 'status' },
      result.publishable
        ? '✔ DE-PARA válido: gera XML válido contra o XSD' + (result.expectedXmlMatches ? ' e igual ao XML esperado.' : '.')
        : '✖ DE-PARA ainda não é válido — veja os diagnósticos.'));

    if (diagnostics.length > 0) {
      parts.push(h('h4', {}, 'Diagnósticos'),
        h('ul', { class: 'diagnostics' }, diagnostics.map((d) => {
          const path = xpathToPath(d.destinationXPath);
          return h('li', { class: `diag severity-${d.severity}` },
            h('span', { class: 'badge' }, SEVERITY_LABEL[d.severity] ?? d.severity),
            ' ',
            h('code', {}, d.code),
            ' ',
            path
              ? h('button', { type: 'button', class: 'link', onclick: () => onSelectPath(path), title: 'Ir para o campo' }, d.destinationXPath)
              : null,
            h('div', {}, d.message),
            d.suggestion && h('div', { class: 'hint' }, `Sugestão: ${d.suggestion}`));
        })));
    }

    if (generation?.xml) {
      // Compara com o XML esperado usado NESTE teste (não o que está no campo agora).
      const testedExpected = result.testedExpectedXml ?? '';
      const comparing = Boolean(testedExpected.trim());
      parts.push(h('h4', {}, comparing ? 'Comparação de XML' : 'XML gerado'),
        comparing
          ? h('div', { class: 'xml-compare' },
            h('figure', {}, h('figcaption', {}, 'Gerado'), h('pre', { class: 'xml' }, generation.xml)),
            h('figure', {}, h('figcaption', {}, result.expectedXmlMatches ? 'Esperado (igual)' : 'Esperado (diferente)'), h('pre', { class: 'xml' }, testedExpected)))
          : h('pre', { class: 'xml' }, generation.xml));
    }
  }

  if (mappingTemplate) {
    parts.push(h('details', { class: 'template-json' },
      h('summary', {}, 'Ver regra montada (somente leitura)'),
      h('pre', {}, JSON.stringify(mappingTemplate, null, 2))));
  }

  return h('div', { 'aria-live': 'polite' }, parts);
}
