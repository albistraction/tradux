// Testes dos módulos puros do editor (node --test). Sem dependências.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { field, literal, operator, describe, validate, initialArguments } from '../../src/Tradux.Web/wwwroot/js/model/ruleNodes.js';
import { group, clause, describeCondition, isEmptyCondition } from '../../src/Tradux.Web/wwwroot/js/model/conditions.js';
import {
  scopeNode, findNode, flatten, valueFields, collectionFields, withRootFields,
} from '../../src/Tradux.Web/wwwroot/js/model/sourceTree.js';
import {
  computeStatus, scopeChain, validateDraft, summarize, pruneMappings, statusCounts, removeSubtree, findDestination, withCollection,
} from '../../src/Tradux.Web/wwwroot/js/model/draft.js';
import {
  buildMappingTemplate, mappingsFromTemplate, conditionFromRuleNode, toRuleNode, toConditionRuleNode,
} from '../../src/Tradux.Web/wwwroot/js/model/mappingTemplate.js';
import { xpathToPath, criticalByPath } from '../../src/Tradux.Web/wwwroot/js/model/diagnostics.js';
import { draftProblems, fromStoredDraft } from '../../src/Tradux.Web/wwwroot/js/model/localDraft.js';
import { rowsFromApi, tablesForApi, duplicateKeys } from '../../src/Tradux.Web/wwwroot/js/model/lookupTables.js';

// Árvore de destino no formato de POST /v1/design/schema (exemplo-colecao.xsd + um opcional aninhado).
const scalar = (name, path, minOccurs = 1, extra = {}) => ({
  name, path, kind: 'Scalar', minOccurs, maxOccurs: 1, nillable: false, scalarType: 'String', defaultValue: null,
  contentModel: null, mappable: true, children: [], ...extra,
});

const tree = {
  name: 'PedidoComItens', path: '/PedidoComItens', kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'sequence',
  children: [
    scalar('numero', '/PedidoComItens/numero'),
    {
      name: 'itens', path: '/PedidoComItens/itens', kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'sequence',
      children: [{
        name: 'item', path: '/PedidoComItens/itens/item', kind: 'Complex', minOccurs: 0, maxOccurs: null, mappable: true, contentModel: 'sequence',
        children: [scalar('descricao', '/PedidoComItens/itens/item/descricao'), scalar('valorUnitario', '/PedidoComItens/itens/item/valorUnitario')],
      }],
    },
    {
      name: 'entrega', path: '/PedidoComItens/entrega', kind: 'Complex', minOccurs: 0, maxOccurs: 1, mappable: true, contentModel: 'sequence',
      children: [scalar('cep', '/PedidoComItens/entrega/cep')],
    },
    { name: '(choice)', path: '/PedidoComItens/(choice#1)', kind: 'Group', minOccurs: 0, maxOccurs: 1, mappable: false, contentModel: 'choice', children: [] },
    scalar('valorTotal', '/PedidoComItens/valorTotal'),
  ],
};

// Árvore de origem no formato de POST /v1/design/source-schema.
const sourceFields = {
  name: '(dados)', path: '', type: 'object', children: [
    { name: 'Numero', path: 'Numero', type: 'text' },
    { name: 'Emissao', path: 'Emissao', type: 'date' },
    { name: 'Tomador', path: 'Tomador', type: 'object', children: [
      { name: 'Cnpj', path: 'Tomador.Cnpj', type: 'text' },
      { name: 'Endereco', path: 'Tomador.Endereco', type: 'object', children: [{ name: 'Cidade', path: 'Tomador.Endereco.Cidade', type: 'text' }] },
    ] },
    { name: 'Itens', path: 'Itens', type: 'list', item: { name: '(item)', path: '', type: 'object', children: [
      { name: 'Descricao', path: 'Descricao', type: 'text' },
      { name: 'Valor', path: 'Valor', type: 'number' },
      { name: '$index', path: '$index', type: 'number' },
    ] } },
  ],
};

const operators = [
  { name: 'concat', minArguments: 1, maxArguments: null, repeatsLastParameter: true, parameters: [{ name: 'parte', kind: 'Any' }] },
  { name: 'greaterThan', minArguments: 2, maxArguments: 2, parameters: [{ name: 'esquerda' }, { name: 'direita' }] },
  { name: 'equals', minArguments: 2, maxArguments: 2, parameters: [{ name: 'esquerda' }, { name: 'direita' }] },
  { name: 'isEmpty', minArguments: 1, maxArguments: 1, parameters: [{ name: 'valor' }] },
  { name: 'and', minArguments: 1, maxArguments: null, repeatsLastParameter: true, parameters: [{ name: 'condição' }] },
  { name: 'or', minArguments: 1, maxArguments: null, repeatsLastParameter: true, parameters: [{ name: 'condição' }] },
  { name: 'not', minArguments: 1, maxArguments: 1, parameters: [{ name: 'condição' }] },
  { name: 'setDefault', minArguments: 2, maxArguments: 2, parameters: [{ name: 'valor' }, { name: 'padrão' }] },
  { name: 'lookup', minArguments: 2, maxArguments: 2, parameters: [{ name: 'chave' }, { name: 'tabela', kind: 'LookupTable' }] },
];

test('ValueSpec vira RuleNode com $type do motor', () => {
  const spec = operator('concat', [field('$index'), literal(': '), field('Descricao')]);
  assert.deepEqual(toRuleNode(spec), {
    $type: 'operator',
    operatorName: 'concat',
    arguments: [{ $type: 'field', path: '$index' }, { $type: 'literal', value: ': ' }, { $type: 'field', path: 'Descricao' }],
  });
  assert.equal(describe(spec), 'concat($index, ": ", Descricao)');
});

test('validação acusa campo vazio, operador inexistente e aridade', () => {
  assert.deepEqual(validate(field(''), operators), ['valor: escolha um campo de origem.']);
  assert.match(validate(operator('naoExiste'), operators)[0], /não existe no catálogo/);
  assert.match(validate(operator('greaterThan', [field('A')]), operators)[0], /espera 2 argumento/);
  assert.deepEqual(validate(operator('concat', [field('A'), field('B'), literal('x')]), operators), []);
});

test('argumentos iniciais: tabela de lookup vira literal com a primeira tabela', () => {
  const lookup = operators.find((o) => o.name === 'lookup');
  assert.deepEqual(initialArguments(lookup, ['situacoes']), [field(''), literal('situacoes')]);
});

test('query builder: E/OU/negação aninhados viram and/or/not', () => {
  const condition = group('and', [
    clause('Numero', 'equals', literal('1')),
    group('or', [clause('Serie', 'isEmpty'), clause('Valor', 'greaterThan', literal(10))], true),
  ]);

  assert.deepEqual(toConditionRuleNode(condition), {
    $type: 'operator', operatorName: 'and', arguments: [
      { $type: 'operator', operatorName: 'equals', arguments: [{ $type: 'field', path: 'Numero' }, { $type: 'literal', value: '1' }] },
      { $type: 'operator', operatorName: 'not', arguments: [{
        $type: 'operator', operatorName: 'or', arguments: [
          { $type: 'operator', operatorName: 'isEmpty', arguments: [{ $type: 'field', path: 'Serie' }] },
          { $type: 'operator', operatorName: 'greaterThan', arguments: [{ $type: 'field', path: 'Valor' }, { $type: 'literal', value: 10 }] },
        ],
      }] },
    ],
  });
  assert.equal(describeCondition(condition), '(Numero = "1" E NÃO (Serie está vazio OU Valor > 10))');
});

test('grupo vazio não gera condição; grupo de uma linha não gera and', () => {
  assert.equal(toConditionRuleNode(group()), null);
  assert.ok(isEmptyCondition(group('and', [group('or')])));
  assert.equal(toConditionRuleNode(group('and', [clause('A', 'isEmpty')])).operatorName, 'isEmpty');
});

test('MappingTemplate: escalar, padrão, condição, complexo implícito e for each com filtro', () => {
  const mappings = {
    '/PedidoComItens/numero': { value: field('Numero'), defaultValue: literal('SEM-NUMERO') },
    '/PedidoComItens/itens/item': { forEach: { collectionPath: 'Itens', filter: group('and', [clause('ValorUnitario', 'greaterThan', literal(0))]) } },
    '/PedidoComItens/itens/item/descricao': { value: operator('concat', [field('$index'), literal(': '), field('Descricao')]) },
    '/PedidoComItens/itens/item/valorUnitario': { value: field('ValorUnitario'), condition: group('and', [clause('Gratis', 'isEmpty')]) },
    '/PedidoComItens/valorTotal': { value: field('ValorTotal') },
  };

  const template = buildMappingTemplate(tree, mappings);

  assert.deepEqual(template.fields.map((f) => f.destinationElementName), ['numero', 'valorTotal']);
  assert.equal(template.fields[0].value.operatorName, 'setDefault');
  assert.equal(template.complex.length, 1, 'entrega sem mapeamento não aparece; itens aparece');
  const collection = template.complex[0].content.collections[0];
  assert.equal(collection.sourceCollectionPath, 'Itens');
  assert.equal(collection.filter.operatorName, 'greaterThan');
  assert.equal(collection.itemContent.fields[1].condition.operatorName, 'isEmpty');
});

test('situação: obrigatório sem mapeamento, opcional sob pai opcional, grupo não suportado', () => {
  const status = computeStatus(tree, { '/PedidoComItens/numero': { value: field('Numero') } });

  assert.equal(status.get('/PedidoComItens/numero'), 'mapped');
  assert.equal(status.get('/PedidoComItens/valorTotal'), 'missing');
  assert.equal(status.get('/PedidoComItens/itens/item'), 'optional');
  assert.equal(status.get('/PedidoComItens/itens/item/descricao'), 'optional', 'bloco sem for each: filhos não são exigidos');
  assert.equal(status.get('/PedidoComItens/entrega/cep'), 'optional', 'complexo opcional sem mapeamento');
  assert.deepEqual(statusCounts(tree, {}), { missing: 2, blocked: 0, inactive: 0 });

  const withDelivery = computeStatus(tree, { '/PedidoComItens/itens/item': { forEach: { collectionPath: 'Itens' } } });
  assert.equal(withDelivery.get('/PedidoComItens/itens/item/descricao'), 'missing', 'for each configurado exige os filhos');
});

test('escopo: dentro do for each os campos são do item da coleção', () => {
  const mappings = { '/PedidoComItens/itens/item': { forEach: { collectionPath: 'Itens' } } };
  assert.deepEqual(scopeChain(tree, mappings, '/PedidoComItens/itens/item/descricao'), ['Itens']);
  assert.deepEqual(scopeChain(tree, mappings, '/PedidoComItens/itens/item'), [], 'o bloco escolhe a coleção no escopo do pai');
  assert.deepEqual(scopeChain(tree, mappings, '/PedidoComItens/numero'), []);
});

test('campos de origem: caminho com ponto, item da lista como escopo, seletores de valor e de coleção', () => {
  assert.equal(findNode(sourceFields, 'Tomador.Endereco.Cidade').path, 'Tomador.Endereco.Cidade');
  assert.equal(findNode(sourceFields, 'Tomador.Inexistente'), null);
  assert.deepEqual(scopeNode(sourceFields, ['Itens']).children.map((c) => c.name), ['Descricao', 'Valor', '$index']);
  assert.equal(scopeNode(sourceFields, ['Numero']), null, 'campo simples não é coleção');

  assert.deepEqual(valueFields(sourceFields).map((f) => f.label),
    ['Numero (texto)', 'Emissao (data)', 'Tomador.Cnpj (texto)', 'Tomador.Endereco.Cidade (texto)'], 'objetos e listas não entram; não desce em listas');
  assert.deepEqual(collectionFields(sourceFields), [
    { path: 'Tomador', label: 'Tomador (objeto único: 1 item)', single: true },
    { path: 'Tomador.Endereco', label: 'Tomador.Endereco (objeto único: 1 item)', single: true },
    { path: 'Itens', label: 'Itens' },
  ]);
});

test('dentro de um for each os campos da raiz entram com scope "root"; fora dele, não', () => {
  const itemFields = valueFields(scopeNode(sourceFields, ['Itens']));
  const inside = withRootFields(itemFields, ['Itens'], sourceFields);
  assert.deepEqual(inside.find((f) => f.scope === 'root'), { path: 'Numero', label: '(raiz) Numero (texto)', scope: 'root' });
  assert.equal(withRootFields(itemFields, [], sourceFields), itemFields);
});

test('XPath de diagnóstico casa com o caminho do nó; rascunho validado e resumido', () => {
  assert.equal(xpathToPath('/PedidoComItens/itens/item[2]/descricao'), '/PedidoComItens/itens/item/descricao');

  const mappings = {
    '/PedidoComItens/numero': { value: field('') },
    '/PedidoComItens/itens/item': { forEach: { collectionPath: '' } },
    '/Removido/x': { value: field('A') },
  };
  const problems = validateDraft(tree, mappings, operators);
  assert.equal(problems.length, 2);
  assert.deepEqual(Object.keys(pruneMappings(tree, mappings)), ['/PedidoComItens/numero', '/PedidoComItens/itens/item']);
  assert.equal(summarize({ value: field('Numero'), defaultValue: literal('X') }), 'Numero (padrão "X")');
});

// ---- Correções da revisão independente da Fase 6 ----

test('B1: complexo obrigatório é emitido mesmo sem filho mapeado (envelope <itens/>)', () => {
  const template = buildMappingTemplate(tree, {
    '/PedidoComItens/numero': { value: field('Numero') },
    '/PedidoComItens/valorTotal': { value: field('ValorTotal') },
  });

  assert.deepEqual(template.complex, [{ destinationElementName: 'itens', content: { fields: [], complex: [], collections: [] } }]);
  assert.equal(statusCounts(tree, {
    '/PedidoComItens/numero': { value: field('Numero') },
    '/PedidoComItens/valorTotal': { value: field('ValorTotal') },
  }).missing, 0, 'tela e servidor concordam: nada obrigatório faltando');
});

test('A5: mapeamento dentro de bloco sem coleção é "inactive", fica fora do template e é contado', () => {
  const mappings = { '/PedidoComItens/itens/item/descricao': { value: field('Descricao') } };

  assert.equal(computeStatus(tree, mappings).get('/PedidoComItens/itens/item/descricao'), 'inactive');
  assert.equal(statusCounts(tree, mappings).inactive, 1);
  assert.deepEqual(buildMappingTemplate(tree, mappings).complex[0].content.collections, []);
  assert.deepEqual(Object.keys(removeSubtree({ ...mappings, '/PedidoComItens/itens/item': { forEach: { collectionPath: 'Itens' } }, '/PedidoComItens/numero': {} },
    '/PedidoComItens/itens/item')), ['/PedidoComItens/numero']);
});

test('B2: obrigatório não mapeável (choice) bloqueia e aparece nos obrigatórios pendentes', () => {
  const blockedTree = {
    ...tree,
    children: [...tree.children, {
      name: 'tomador', path: '/PedidoComItens/tomador', kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: false, contentModel: 'choice',
      children: [scalar('cnpj', '/PedidoComItens/tomador/cnpj', 1, { mappable: false })],
    }],
  };

  const status = computeStatus(blockedTree, {});
  assert.equal(status.get('/PedidoComItens/tomador'), 'blocked');
  assert.equal(status.get('/PedidoComItens/tomador/cnpj'), 'unsupported');
  assert.equal(statusCounts(blockedTree, {}).blocked, 1);
});

const current = { templateId: 'dps-erp', isNew: true };

test('A4: edição guardada no navegador com forma inválida é recusada antes de tocar no estado', () => {
  assert.deepEqual(draftProblems({ current }), []);
  assert.deepEqual(draftProblems({ current: { templateId: 'com espaço' } }), ['template em edição inválido.']);
  assert.ok(draftProblems([]).length > 0);
  assert.ok(draftProblems({ current, tree: {} }).length > 0);
  assert.ok(draftProblems({ current, tree: { root: { name: 'R', path: '/R', kind: 'Complex', children: [] } } })
    .some((p) => p.includes('schema')));
  assert.ok(draftProblems({ current, mappings: [] }).length > 0);
  assert.ok(draftProblems({ current, lookupTables: { moedas: { BRL: 'R$' } } }).length > 0, 'tabelas são guardadas como linhas');
  assert.ok(draftProblems({ current, tree: { root: { name: 'R', path: '/R', kind: 'Complex', children: [{ nome: 'x' }] } }, schema: { files: [], entryPoint: 'x' } }).length > 0);
  assert.throws(() => fromStoredDraft({ current, mappings: [] }), /mapeamentos inválidos/);
});

test('edição guardada volta ao editor: mapeamento sem destino na árvore cai; relatório de troca inválido é ignorado', () => {
  const stored = JSON.parse(JSON.stringify({
    current, schema: { files: [], entryPoint: 'd.xsd' }, tree: { root: tree }, lookupTables: { moedas: [['BRL', 'R$']] },
    mappings: { '/PedidoComItens/numero': { value: field('Numero') }, '/Sumiu/x': { value: field('X') } },
    schemaChanges: { report: {}, orphans: [] },
  }));
  const fields = fromStoredDraft(stored);
  assert.deepEqual(Object.keys(fields.mappings), ['/PedidoComItens/numero']);
  assert.equal(fields.selectedPath, '/PedidoComItens');
  assert.equal(fields.schemaChanges, null);
  assert.deepEqual(fields.lookupTables, { moedas: [['BRL', 'R$']] });
});

// ---- Fase 8: relatório dos testes estruturais ----
import { summarizeQuality, describeFailure, CATEGORY_LABEL } from '../../src/Tradux.Web/wwwroot/js/model/quality.js';

test('summarizeQuality agrupa por categoria na ordem da seção 19 e separa reprovados', () => {
  const report = {
    cases: [
      { name: 'invalido:pattern:Cnpj', category: 'Invalid', expectedValid: false, passed: true },
      { name: 'completo', category: 'Complete', expectedValid: true, passed: false, failureCode: 'XSD_VALIDATION', destinationXPath: '/DPS/serie' },
      { name: 'obrigatorios', category: 'Required', expectedValid: true, passed: true },
      { name: 'invalido:maxLength:Serie', category: 'Invalid', expectedValid: false, passed: false, target: 'Serie (maxLength)', failureCode: 'INVALID_CASE_ACCEPTED' },
    ],
    notCovered: [],
  };

  const groups = summarizeQuality(report);

  assert.deepEqual(groups.map((g) => [g.category, g.total, g.passed]), [['Required', 1, 1], ['Complete', 1, 0], ['Invalid', 2, 1]]);
  assert.equal(groups[1].label, CATEGORY_LABEL.Complete);
  assert.match(describeFailure(groups[1].failed[0]), /completo: entrada válida não gerou XML válido \(XSD_VALIDATION\)/);
  assert.match(describeFailure(groups[2].failed[0]), /entrada inválida foi aceita \(Serie \(maxLength\)\)/);
  assert.deepEqual(summarizeQuality(null), []);
});

// ---- Fase 22 (ADR-0025): publicação como próxima versão do template no serviço ----
import { buildPublication, describePublication, sampleScenario, TEMPLATE_NAME } from '../../src/Tradux.Web/wwwroot/js/model/publish.js';

test('buildPublication exige nome, XSD de origem e de destino, XML de exemplo e XML esperado; vai para PUT /v1/templates/{nome}', () => {
  const empty = buildPublication({});
  assert.equal(empty.request, null);
  assert.equal(empty.problems.length, 5);

  const sourceSchema = { files: [{ name: 'o.xsd', contentBase64: 'eA==' }], entryPoint: 'o.xsd' };
  const base = { schema: { files: [], entryPoint: 'd.xsd' }, mapping: { fields: [] }, sourceSchema, sampleXml: '<N/>', expectedXml: '<R/>', lookupTables: { t: { a: 'b' } } };
  const ok = buildPublication({ ...base, templateId: ' dps-erp ' });
  assert.deepEqual(ok.problems, []);
  assert.equal(ok.request.templateId, 'dps-erp');
  assert.deepEqual(ok.request.body.sourceSchema, sourceSchema);
  assert.deepEqual(ok.request.body.lookupTables, { t: { a: 'b' } });
  assert.deepEqual(ok.request.body.scenarios, [{ name: 'XML de exemplo do editor', sourceXml: '<N/>', expectedXml: '<R/>' }]);

  assert.match(buildPublication({ ...base, templateId: 'com espaço' }).problems[0], /Nome do template/);
  assert.ok(TEMPLATE_NAME.test('webservice-destino_v2.0'));
  assert.ok(!TEMPLATE_NAME.test('-x'));
});

test('describePublication resume sucesso e recusa sem inventar versão', () => {
  assert.deepEqual(describePublication({ success: true, snapshotVersion: 3 }), { ok: true, text: 'Publicado: versão 3 ativa.' });
  assert.match(describePublication({ success: false, diagnostics: [{ code: 'SCENARIO_FAILED' }, { code: 'SCENARIO_FAILED' }] }).text, /\(SCENARIO_FAILED\)/);
  assert.equal(describePublication(null), null);
});

test('edição no navegador: forma do XSD de origem validada', () => {
  assert.deepEqual(draftProblems({ current, sourceTree: { root: { children: [] } } }), ['XSD de origem ausente ou inválido.']);
  assert.deepEqual(draftProblems({ current, sampleXml: '<N/>', sourceSchema: { files: [], entryPoint: 'o.xsd' }, sourceTree: { root: { children: [] } } }), []);
});

// ---- Arquivos: XSDs em mais de uma escolha (principal + xmldsig importado) e XML na codificação declarada ----
import { mergeFiles, removeFile, decodeXml } from '../../src/Tradux.Web/wwwroot/js/model/files.js';

test('mergeFiles acumula escolhas, mesmo nome substitui, ordem da 1ª escolha mantida; removeFile tira pelo nome', () => {
  const principal = { name: 'destino.xsd', v: 1 };
  const dsig = { name: 'xmldsig-core-schema.xsd' };
  const merged = mergeFiles([principal], [dsig]);
  assert.deepEqual(merged.map((f) => f.name), ['destino.xsd', 'xmldsig-core-schema.xsd']);
  assert.equal(mergeFiles(merged, [{ name: 'destino.xsd', v: 2 }])[0].v, 2);
  assert.deepEqual(removeFile(merged, 'destino.xsd').map((f) => f.name), ['xmldsig-core-schema.xsd']);
});

test('decodeXml respeita o encoding do prólogo (ISO-8859-1) e usa UTF-8 sem declaração', () => {
  const bytes = (text) => [...text].map((c) => c.charCodeAt(0));
  const latin1 = Uint8Array.from([...bytes('<?xml version="1.0" encoding="ISO-8859-1"?><n>S'), 0xe3, ...bytes('o</n>')]);
  assert.match(decodeXml(latin1.buffer), /<n>São<\/n>/);
  assert.equal(decodeXml(new TextEncoder().encode('<n>São</n>').buffer), '<n>São</n>');
});

// ---- Fase 14 (ADR-0017): troca do XSD de destino ----
import { applySchemaChanges, moveOrphan, discardOrphan, changeMarks, validSchemaChanges } from '../../src/Tradux.Web/wwwroot/js/model/schemaChanges.js';

const block = (name, path, children, extra = {}) => ({
  name, path, kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'sequence', children, ...extra,
});
const v2 = block('Nota2', '/Nota2', [
  scalar('Numero', '/Nota2/Numero'),
  block('Tomador', '/Nota2/Tomador', [scalar('RazaoSocial', '/Nota2/Tomador/RazaoSocial'), scalar('Cnpj', '/Nota2/Tomador/Cnpj')]),
  block('Itens', '/Nota2/Itens', [scalar('Cod', '/Nota2/Itens/Cod')], { maxOccurs: null }),
  block('Servico', '/Nota2/Servico', [scalar('Desc', '/Nota2/Servico/Desc')]),
]);
const report = {
  beforeRootElementName: 'Nota', afterRootElementName: 'Nota2', identical: false,
  summary: { remap: 2, map: 0, review: 0, info: 1 },
  changes: [
    { path: '/Nota2', kind: 'RootChanged', impact: 'Info', message: 'raiz' },
    { path: '/Nota/Tomador/Razao', kind: 'Removed', impact: 'Remap', message: 'saiu', suggestions: ['/Nota2/Tomador/RazaoSocial'] },
    { path: '/Nota2/Servico', kind: 'Moved', impact: 'Remap', beforePath: '/Nota/Bloco/Servico', message: 'moveu' },
    { path: '/Nota2/Tomador/Cnpj', kind: 'TypeChanged', impact: 'Review', message: 'tipo' },
  ],
};
const v1Mappings = {
  '/Nota/Numero': { value: field('Numero') },
  '/Nota/Tomador/Razao': { value: field('Nome') },
  '/Nota/Bloco/Servico/Desc': { value: field('Descricao') },
  '/Nota/Itens': { forEach: { collectionPath: 'Itens' } },
};

test('troca de XSD: raiz renomeada acompanha; campo que sumiu vira pendente com sugestão (nada some em silêncio)', () => {
  const { mappings, orphans } = applySchemaChanges(v2, v1Mappings, report);

  assert.deepEqual(Object.keys(mappings).sort(), ['/Nota2/Itens', '/Nota2/Numero']);
  assert.deepEqual(orphans.map((o) => [o.path, o.suggestions]), [
    ['/Nota/Tomador/Razao', ['/Nota2/Tomador/RazaoSocial']],
    ['/Nota/Bloco/Servico/Desc', ['/Nota2/Servico/Desc']], // dentro do bloco movido
  ]);
});

test('mover pendente: só para campo compatível e livre; descartar; marcas na árvore', () => {
  const { mappings, orphans } = applySchemaChanges(v2, v1Mappings, report);

  assert.match(moveOrphan(v2, mappings, orphans, '/Nota/Tomador/Razao', '/Nota2/Tomador').error, /campo de valor/);
  assert.match(moveOrphan(v2, mappings, orphans, '/Nota/Tomador/Razao', '/Nota2/Numero').error, /já está mapeado/);
  const moved = moveOrphan(v2, mappings, orphans, '/Nota/Tomador/Razao', '/Nota2/Tomador/RazaoSocial');
  assert.equal(moved.mappings['/Nota2/Tomador/RazaoSocial'].value.path, 'Nome');
  assert.deepEqual(moved.orphans.map((o) => o.path), ['/Nota/Bloco/Servico/Desc']);
  assert.deepEqual(discardOrphan(moved.orphans, '/Nota/Bloco/Servico/Desc'), []);

  const marks = changeMarks(report);
  assert.equal(marks.get('/Nota2/Servico'), 'Remap');
  assert.equal(marks.get('/Nota2/Tomador/Cnpj'), 'Review');
  assert.equal(marks.has('/Nota/Tomador/Razao'), false); // removido: não está na árvore nova
  assert.equal(validSchemaChanges({ report, orphans }), true);
  assert.equal(validSchemaChanges({ report: {}, orphans: [] }), false);
});

import { inactiveMappings, mergeOrphans } from '../../src/Tradux.Web/wwwroot/js/model/schemaChanges.js';

test('bloco que passou a repetir: mapeamento dentro dele aparece como inativo até definir o for each (revisão C1)', () => {
  const root = block('R', '/R', [block('B', '/R/B', [scalar('x', '/R/B/x')], { minOccurs: 0, maxOccurs: null })]);
  const repeatReport = { beforeRootElementName: 'R', afterRootElementName: 'R', summary: { remap: 1, map: 0, review: 0, info: 0 },
    changes: [{ path: '/R/B', kind: 'BecameRepeatable', impact: 'Remap', message: 'repete' }] };
  const { mappings, orphans } = applySchemaChanges(root, { '/R/B/x': { value: field('X') } }, repeatReport);

  assert.deepEqual(orphans, []);
  assert.deepEqual(inactiveMappings(root, mappings), [{ path: '/R/B/x', block: '/R/B' }]);
  assert.deepEqual(inactiveMappings(root, { ...mappings, '/R/B': { forEach: { collectionPath: 'Itens' } } }), []);
});

test('bloco que deixou de repetir: os filhos do antigo for each viram pendentes para conferir (revisão C1)', () => {
  const root = block('R', '/R', [block('B', '/R/B', [scalar('x', '/R/B/x')])]);
  const stopReport = { beforeRootElementName: 'R', afterRootElementName: 'R', summary: { remap: 1, map: 0, review: 0, info: 0 },
    changes: [{ path: '/R/B', kind: 'StoppedRepeating', impact: 'Remap', message: 'parou' }] };
  const { mappings, orphans } = applySchemaChanges(root, { '/R/B': { forEach: { collectionPath: 'Itens' } }, '/R/B/x': { value: field('Cod') } }, stopReport);

  assert.deepEqual(mappings, {});
  const child = orphans.find((o) => o.path === '/R/B/x');
  assert.deepEqual(child.suggestions, ['/R/B/x']); // confirmar no mesmo campo
  assert.match(child.reason, /deixou de repetir/);
  assert.ok(orphans.some((o) => o.path === '/R/B')); // o for each em si
  assert.deepEqual(mergeOrphans(orphans, orphans).length, orphans.length); // troca repetida não duplica
  assert.equal(validSchemaChanges({ report: stopReport, orphans: [{ path: '/R/B/x', suggestions: [] }] }), false); // sem entry
  assert.equal(validSchemaChanges({ report: stopReport, orphans }), true);
});

test('revisão 2: mesmo caminho com mapeamentos diferentes não some; ignorado que passaria a valer vira pendente', () => {
  const merged = mergeOrphans([{ path: '/R/B/x', entry: { value: field('OLD') }, suggestions: [] }], [{ path: '/R/B/x', entry: { value: field('NEW') }, suggestions: [] }]);
  assert.deepEqual(merged.map((o) => [o.id, o.entry.value.path]), [['/R/B/x', 'OLD'], ['/R/B/x (2)', 'NEW']]);
  assert.deepEqual(discardOrphan(merged, '/R/B/x (2)').map((o) => o.entry.value.path), ['OLD']);

  const oldRoot = block('R', '/R', [block('B', '/R/B', [scalar('x', '/R/B/x')], { maxOccurs: null })]);
  const newRoot = block('R', '/R', [block('B', '/R/B', [scalar('x', '/R/B/x')])]);
  const info = { beforeRootElementName: 'R', afterRootElementName: 'R', summary: { remap: 0, map: 0, review: 0, info: 1 },
    changes: [{ path: '/R/B', kind: 'StoppedRepeating', impact: 'Info', message: 'parou' }] };
  const { mappings, orphans } = applySchemaChanges(newRoot, { '/R/B/x': { value: field('Cod') } }, info, oldRoot);
  assert.deepEqual(mappings, {});
  assert.deepEqual(orphans.map((o) => [o.path, o.suggestions]), [['/R/B/x', ['/R/B/x']]]);
  assert.match(orphans[0].reason, /Estava ignorado/);
});

// ---- Fase 16 (ADR-0020): escopo raiz tipado e objeto único explícito ----

test('campo da raiz vira RuleNode com scope "root"; o padrão não leva scope', () => {
  assert.deepEqual(toRuleNode(field('lote')), { $type: 'field', path: 'lote' });
  assert.deepEqual(toRuleNode(field('lote', 'root')), { $type: 'field', path: 'lote', scope: 'root' });
  assert.equal(describe(field('lote', 'root')), '(raiz) lote');
});

test('for each sobre objeto único: escopo do item é o objeto e o template leva singleObjectAsItem', () => {
  const item = scopeNode(sourceFields, ['Tomador']);
  assert.deepEqual(flatten(item).map((n) => n.path), ['Cnpj', 'Endereco', 'Endereco.Cidade', '$index']);

  // A opção de objeto único é explícita e troca sem perder o resto do bloco (ADR-0020).
  const filter = group('and', [clause('Cnpj', 'notEmpty')]);
  assert.deepEqual(withCollection({ collectionPath: 'Itens', filter }, 'Tomador', true), { collectionPath: 'Tomador', filter, singleObject: true });
  assert.deepEqual(withCollection({ collectionPath: 'Tomador', singleObject: true }, 'Itens', false), { collectionPath: 'Itens' });

  const mappings = {
    '/PedidoComItens/numero': { value: field('lote') },
    '/PedidoComItens/itens/item': { forEach: { collectionPath: 'infDPS', singleObject: true } },
    '/PedidoComItens/itens/item/descricao': { value: field('numero') },
    '/PedidoComItens/itens/item/valorUnitario': { value: field('lote', 'root') },
  };
  const collection = buildMappingTemplate(tree, mappings).complex[0].content.collections[0];
  assert.equal(collection.singleObjectAsItem, true);
  assert.deepEqual(collection.itemContent.fields[1].value, { $type: 'field', path: 'lote', scope: 'root' });
  assert.equal(buildMappingTemplate(tree, { ...mappings, '/PedidoComItens/itens/item': { forEach: { collectionPath: 'infDPS' } } })
    .complex[0].content.collections[0].singleObjectAsItem, undefined);
});

// ---- Fase 19: elemento com conteúdo xs:choice direto (ramos sem nó de grupo), ex.: Rps do webserviceNFSe ----

test('ramos de um elemento com conteúdo choice são opcionais; o choice exige ao menos um ramo', () => {
  const branch = (name) => ({
    name, path: `/R/Rps/${name}`, kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'sequence',
    children: [scalar('v', `/R/Rps/${name}/v`)],
  });
  const choiceTree = {
    name: 'R', path: '/R', kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'sequence',
    children: [{
      name: 'Rps', path: '/R/Rps', kind: 'Complex', minOccurs: 1, maxOccurs: null, mappable: true, contentModel: 'choice', contentRepeatable: true,
      children: [branch('A'), branch('B')],
    }],
  };

  const onlyA = { '/R/Rps': { forEach: { collectionPath: 'itens' } }, '/R/Rps/A/v': { value: field('x') } };
  const rps = buildMappingTemplate(choiceTree, onlyA).collections[0];
  assert.deepEqual(rps.itemContent.complex.map((c) => c.destinationElementName), ['A']); // B vazio não é emitido
  const status = computeStatus(choiceTree, onlyA);
  assert.equal(status.get('/R/Rps'), 'mapped');
  assert.equal(status.get('/R/Rps/B/v'), 'optional'); // ramo não escolhido não é pendência

  const noBranch = computeStatus(choiceTree, { '/R/Rps': { forEach: { collectionPath: 'itens' } } });
  assert.equal(noBranch.get('/R/Rps'), 'missing'); // choice sem ramo mapeado
});

test('choice direto não repetível (obrigatório, opcional), aninhado e na raiz', () => {
  const leaf = (name, path, minOccurs = 1) => scalar(name, path, minOccurs);
  const choiceNode = (name, path, minOccurs, children) => ({ name, path, kind: 'Complex', minOccurs, maxOccurs: 1, mappable: true, contentModel: 'choice', children });
  const seq = (name, path, children) => ({ name, path, kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'sequence', children });
  const t = seq('R', '/R', [
    choiceNode('doc', '/R/doc', 1, [leaf('cpf', '/R/doc/cpf'), seq('cad', '/R/doc/cad', [leaf('im', '/R/doc/cad/im')])]),
    choiceNode('opt', '/R/opt', 0, [leaf('x', '/R/opt/x'), leaf('y', '/R/opt/y')]),
    choiceNode('ext', '/R/ext', 1, [choiceNode('inner', '/R/ext/inner', 1, [leaf('p', '/R/ext/inner/p'), leaf('q', '/R/ext/inner/q')]), leaf('z', '/R/ext/z')]),
  ]);

  // Nada mapeado: choice obrigatório pendente; opcional não; ramos nunca pendentes sozinhos.
  const empty = computeStatus(t, {});
  assert.equal(empty.get('/R/doc'), 'missing');
  assert.equal(empty.get('/R/doc/cpf'), 'optional');
  assert.equal(empty.get('/R/doc/cad/im'), 'optional');
  assert.equal(empty.get('/R/opt'), 'container');
  assert.equal(empty.get('/R/ext'), 'missing');

  // Um ramo escolhido: só ele é emitido (ramo complexo obrigatório não sai vazio); aninhado conta para o externo.
  const chosen = { '/R/doc/cpf': { value: field('Cpf') }, '/R/ext/inner/q': { value: field('Q') } };
  const status = computeStatus(t, chosen);
  assert.equal(status.get('/R/doc'), 'container');
  assert.equal(status.get('/R/ext'), 'container');
  const template = buildMappingTemplate(t, chosen);
  const doc = template.complex.find((c) => c.destinationElementName === 'doc');
  assert.deepEqual(doc.content.complex, []);
  assert.deepEqual(doc.content.fields.map((f) => f.destinationElementName), ['cpf']);
  assert.equal(template.complex.some((c) => c.destinationElementName === 'opt'), false);

  // Raiz com conteúdo choice: ramo complexo obrigatório não mapeado não é emitido.
  const rootChoice = { ...choiceNode('R', '/R', 1, [seq('a', '/R/a', [leaf('v', '/R/a/v')]), seq('b', '/R/b', [leaf('w', '/R/b/w')])]) };
  assert.deepEqual(buildMappingTemplate(rootChoice, { '/R/a/v': { value: field('V') } }).complex.map((c) => c.destinationElementName), ['a']);
});

test('assinatura (ds:Signature) não é exigida no DE-PARA: quem chama o serviço assina', () => {
  const tree = {
    name: 'Nota', path: '/Nota', kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, children: [
      { name: '(choice)', path: '/Nota/(choice#1)', kind: 'Group', contentModel: 'choice', minOccurs: 0, maxOccurs: 1, mappable: true, children: [
        { name: 'ChaveDigital', path: '/Nota/ChaveDigital', kind: 'Scalar', minOccurs: 1, maxOccurs: 1, mappable: true, children: [] },
        { name: 'Signature', path: '/Nota/Signature', kind: 'Complex', minOccurs: 0, maxOccurs: 1, mappable: false, signedByCaller: true, children: [] },
      ] },
    ],
  };
  const status = computeStatus(tree, {});
  assert.equal(status.get('/Nota/Signature'), 'signature');
  assert.equal(status.get('/Nota/(choice#1)'), 'container');
  assert.deepEqual(statusCounts(tree, {}), { missing: 0, blocked: 0, inactive: 0 });
});

test('campo dentro de grupo (choice de sequences) é encontrado pelo caminho do elemento pai', () => {
  const tree = {
    name: 'Rps', path: '/Rps', kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'choice', children: [
      { name: '(sequence)', path: '/Rps/(sequence#1)', kind: 'Group', contentModel: 'sequence', minOccurs: 1, maxOccurs: 1, mappable: true, children: [
        { name: 'Numero', path: '/Rps/Numero', kind: 'Scalar', minOccurs: 1, maxOccurs: 1, mappable: true, children: [] },
      ] },
      { name: '(sequence)', path: '/Rps/(sequence#2)', kind: 'Group', contentModel: 'sequence', minOccurs: 1, maxOccurs: 1, mappable: true, children: [
        { name: 'Itens', path: '/Rps/Itens', kind: 'Complex', minOccurs: 0, maxOccurs: null, mappable: true, children: [
          { name: 'Valor', path: '/Rps/Itens/Valor', kind: 'Scalar', minOccurs: 1, maxOccurs: 1, mappable: true, children: [] },
        ] },
      ] },
    ],
  };
  assert.equal(findDestination(tree, '/Rps/Numero')?.name, 'Numero');
  assert.equal(findDestination(tree, '/Rps/Itens/Valor')?.name, 'Valor');
  assert.deepEqual(scopeChain(tree, { '/Rps/Itens': { forEach: { collectionPath: 'Linhas' } } }, '/Rps/Itens/Valor'), ['Linhas']);
});

// ---- Fase 22 (ADR-0025): abrir uma versão publicada no editor ----

test('versão publicada volta ao editor: o MappingTemplate refeito é o mesmo (ida e volta)', () => {
  const mappings = {
    '/PedidoComItens/numero': { value: field('Numero'), defaultValue: literal('SEM-NUMERO') },
    '/PedidoComItens/itens/item': {
      forEach: {
        collectionPath: 'Itens',
        filter: group('or', [clause('ValorUnitario', 'greaterThan', literal(0)), group('and', [clause('Brinde', 'isEmpty')], true)]),
        orderBy: field('Descricao'),
      },
    },
    '/PedidoComItens/itens/item/descricao': { value: operator('concat', [field('$index'), literal(': '), field('Descricao')]) },
    '/PedidoComItens/itens/item/valorUnitario': { value: field('Lote', 'root'), condition: group('and', [clause('Gratis', 'isEmpty')]) },
    '/PedidoComItens/entrega/cep': { value: field('Cep') },
    '/PedidoComItens/valorTotal': { value: operator('lookup', [field('Moeda'), literal('moedas')]) },
  };

  // JSON como a API devolve (sem undefined, números como números).
  const published = JSON.parse(JSON.stringify(buildMappingTemplate(tree, mappings)));
  const reopened = mappingsFromTemplate(tree, published);

  assert.deepEqual(reopened.problems, []);
  assert.deepEqual(buildMappingTemplate(tree, reopened.mappings), published);
  assert.deepEqual(reopened.mappings['/PedidoComItens/numero'], { value: field('Numero'), defaultValue: literal('SEM-NUMERO') });
  assert.equal(reopened.mappings['/PedidoComItens/itens/item'].forEach.filter.combinator, 'or');
});

test('objeto único, campo de choice dentro de grupo e o que não tem destino viram problema (nada some em silêncio)', () => {
  const grouped = {
    name: 'R', path: '/R', kind: 'Complex', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'sequence',
    children: [{ name: '(choice)', path: '/R/(choice#1)', kind: 'Group', minOccurs: 1, maxOccurs: 1, mappable: true, contentModel: 'choice',
      children: [scalar('cpf', '/R/cpf', 1), scalar('cnpj', '/R/cnpj', 1)] }],
  };
  const reopened = mappingsFromTemplate(grouped, {
    fields: [
      { destinationElementName: 'cnpj', value: { $type: 'field', path: 'Tomador.Cnpj' } },
      { destinationElementName: 'sumiu', value: { $type: 'literal', value: 'x' } },
    ],
    complex: [], collections: [],
  });
  assert.deepEqual(reopened.mappings, { '/R/cnpj': { value: field('Tomador.Cnpj') } });
  assert.deepEqual(reopened.problems, ['/R/sumiu: campo sem destino na árvore.']);

  const single = mappingsFromTemplate(tree, { fields: [], complex: [{ destinationElementName: 'itens', content: { fields: [], complex: [], collections: [
    { destinationElementName: 'item', sourceCollectionPath: 'infDPS', singleObjectAsItem: true, itemContent: { fields: [], complex: [], collections: [] } },
  ] } }], collections: [] });
  assert.deepEqual(single.mappings['/PedidoComItens/itens/item'], { forEach: { collectionPath: 'infDPS', singleObject: true } });
});

test('condição que o construtor não representa é recusada com explicação; cenário de exemplo da versão', () => {
  assert.throws(() => conditionFromRuleNode({ $type: 'operator', operatorName: 'contains', arguments: [{ $type: 'field', path: 'a' }, { $type: 'literal', value: 'b' }] }), /contains/);
  const negated = conditionFromRuleNode({ $type: 'operator', operatorName: 'not', arguments: [{ $type: 'operator', operatorName: 'equals', arguments: [{ $type: 'field', path: 'a' }, { $type: 'literal', value: 1 }] }] });
  assert.equal(negated.negated, true);
  assert.equal(negated.children[0].operator, 'equals');

  assert.deepEqual(sampleScenario([{ name: 'só asserções', sourceXml: '<A/>' }, { name: 'completo', sourceXml: '<B/>', expectedXml: '<R/>' }]), { sampleXml: '<B/>', expectedXml: '<R/>' });
  assert.deepEqual(sampleScenario([]), { sampleXml: '', expectedXml: '' });
});

test('tabelas DE-PARA: dicionário da API ⇄ linhas do editor; linha sem chave fica fora; chave repetida é apontada', () => {
  const rows = rowsFromApi({ moedas: { BRL: 'R$', USD: 'US$' } });
  assert.deepEqual(rows, { moedas: [['BRL', 'R$'], ['USD', 'US$']] });
  assert.deepEqual(tablesForApi({ moedas: [...rows.moedas, ['', 'sem chave']] }), { moedas: { BRL: 'R$', USD: 'US$' } });
  assert.deepEqual([...duplicateKeys([['A', '1'], ['', 'x'], ['A', '2'], ['', 'y']])], ['A']);
});

test('diagnósticos críticos marcam o nó pelo caminho, sem repetir; teste desatualizado não marca', () => {
  const d = { severity: 'Critical', code: 'X', destinationXPath: '/P/itens/item[2]/descricao', message: 'm' };
  const result = { compileDiagnostics: [d], generation: { diagnostics: [d, { ...d, severity: 'Warning' }] } };
  assert.deepEqual([...criticalByPath(result).entries()], [['/P/itens/item/descricao', [d]]]);
  assert.equal(criticalByPath({ ...result, stale: true }).size, 0);
});
