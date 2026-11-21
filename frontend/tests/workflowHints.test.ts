import test from 'node:test';
import assert from 'node:assert/strict';
import {
  acceptanceChecklist,
  acceptanceNote,
  captureRules,
  getOrderHints,
  orderTypePhotoTips,
  photoPhases,
  resolveHintRole,
  roleResponsibilities,
  workflowStageIndex,
  workflowStages,
} from '../src/workflowHints.ts';
import type { ApiStatus } from '../src/api.ts';
import type { OrderType } from '../src/domain.ts';

const statuses: ApiStatus[] = [
  'created',
  'published',
  'workerOnTheWay',
  'inProgress',
  'workFinished',
  'rework',
  'approved',
  'completed',
];
const roles = ['worker', 'client', 'dispatcher', 'admin'] as const;

test('этапы покрывают все рабочие статусы, а отмена не строит прогресс', () => {
  assert.equal(workflowStages.length, 7);
  const covered = workflowStages.flatMap((stage) => stage.statuses);
  for (const status of statuses) {
    assert.equal(covered.filter((item) => item === status).length, 1, `статус ${status}`);
    assert.ok(workflowStageIndex(status) >= 0, `индекс ${status}`);
  }
  assert.equal(workflowStageIndex('created'), 0);
  assert.equal(workflowStageIndex('workerOnTheWay'), 2);
  assert.equal(workflowStageIndex('workFinished'), 5);
  assert.equal(workflowStageIndex('completed'), 6);
  assert.equal(workflowStageIndex('cancelled'), -1);
});

test('для каждой роли и этапа есть подсказка со ссылкой на регламент', () => {
  for (const role of roles) {
    for (const status of statuses) {
      const hints = getOrderHints(status, role);
      assert.ok(hints.length > 0, `${role}/${status}`);
      assert.ok(hints.length <= 2, `${role}/${status}: слишком много подсказок`);
      assert.equal(new Set(hints.map((hint) => hint.id)).size, hints.length);
      for (const hint of hints) {
        assert.ok(hint.title.length > 3);
        assert.ok(hint.summary.length > 20);
        assert.match(hint.source ?? '', /^п\./, `${role}/${status}: нет ссылки на пункт`);
        assert.ok(['info', 'action', 'warning', 'success'].includes(hint.tone));
      }
    }
  }
});

test('подсказки соответствуют этапу и роли', () => {
  const worker = getOrderHints('workerOnTheWay', 'worker');
  assert.match(worker[0].title, /до/i);
  assert.equal(worker[0].bullets?.length, 5);
  assert.match(worker[0].bullets?.join(' ') ?? '', /Шильдик/);
  assert.match(worker[1].source ?? '', /152-ФЗ/);

  const dispatch = getOrderHints('workFinished', 'dispatcher');
  assert.match(dispatch[0].summary, /«до» и «после»/);
  assert.match(dispatch[1].summary, /Верните отчёт на доработку/);

  const client = getOrderHints('workFinished', 'client');
  assert.match(client[0].summary, /Сравните кадры/);
  assert.match(client[0].bullets?.join(' ') ?? '', /48 часов/);

  assert.equal(getOrderHints('cancelled', 'worker')[0].id, 'cancelled');
  assert.equal(getOrderHints('cancelled', 'admin')[0].id, 'cancelled');
});

test('фоточек-лист содержит обязательные кадры до и после, процесс и советы по видам работ', () => {
  const before = photoPhases.find((phase) => phase.id === 'before');
  const during = photoPhases.find((phase) => phase.id === 'during');
  const after = photoPhases.find((phase) => phase.id === 'after');
  assert.equal(before?.frames.length, 5);
  assert.equal(after?.frames.length, 5);
  assert.equal(during?.frames.length, 3);
  assert.match(before?.note ?? '', /защищают/);
  assert.match(after?.note ?? '', /сравнить/);

  for (const type of Object.keys(orderTypePhotoTips) as OrderType[]) {
    assert.ok(orderTypePhotoTips[type].length >= 3, type);
  }
  assert.ok(captureRules.some((rule) => rule.includes('152-ФЗ')));
  assert.ok(captureRules.some((rule) => rule.includes('галереи')));
  assert.equal(acceptanceChecklist.length, 4);
  assert.match(acceptanceNote, /48 часов/);
  assert.match(acceptanceNote, /договор/);
  assert.equal(roleResponsibilities.length, 4);
});

test('роль для подсказки выбирается по участию в заявке и полномочиям', () => {
  assert.equal(
    resolveHintRole({
      status: 'inProgress',
      roles: ['Worker', 'Dispatcher'],
      isInitiator: false,
      isExecutor: true,
    }),
    'worker',
  );
  assert.equal(
    resolveHintRole({
      status: 'workFinished',
      roles: ['Dispatcher'],
      isInitiator: true,
      isExecutor: false,
    }),
    'client',
  );
  assert.equal(
    resolveHintRole({
      status: 'workFinished',
      roles: ['Dispatcher'],
      isInitiator: false,
      isExecutor: false,
    }),
    'dispatcher',
  );
  assert.equal(
    resolveHintRole({
      status: 'approved',
      roles: ['SuperAdmin'],
      isInitiator: false,
      isExecutor: false,
    }),
    'admin',
  );
  assert.equal(
    resolveHintRole({
      status: 'published',
      roles: ['Admin'],
      isInitiator: false,
      isExecutor: false,
    }),
    'admin',
  );
  assert.equal(
    resolveHintRole({
      status: 'published',
      roles: ['Worker'],
      isInitiator: false,
      isExecutor: false,
    }),
    'worker',
  );
  assert.equal(
    resolveHintRole({
      status: 'created',
      roles: ['User'],
      isInitiator: true,
      isExecutor: false,
    }),
    'client',
  );
});
