using System.Collections;
using System.Text;
using UnityEngine;

//컨베이어 벨트위의 부품을 집어서,
//베이스의 구멍 방향에 맞게 손목을 돌린뒤,
//구멍에 끼우는 조립 동작을 하는 스크립트 
public class AssemblySequence : MonoBehaviour
{
    #region 1.준비와 대기 위치 
    [UnityEngine.Serialization.FormerlySerializedAs("iK")]
    public ArmIK ik;
    public GripperController gripper;
    public Transform partStopPoint; //부품 벤트 위 멈추는 자리
    public Transform basePoint;     // 베이스 벨트 위 조립 자리

    [Header("대기 위치")]
    public Vector3 homePosition = new Vector3(0.22f, 0.20f, 0f);
    [Tooltip("대기 위치로 돌아갈떄 관절 속도. 매번 같은 관절 자세로 돌아가 손목 꼬임을 풀어줌")]
    public float homeJointSpeed = 90f;

    public bool IsBusy { get; private set; }

    public string Status { get; private set; } = "대기";  //현재 단계 화면 표시용

    private BaseInfo currentBase;   //현재 조립 대상 베이스 

    private float[] homeJoints; // 처음 대기 위치의 관절 각도 6개 (매 사이클 여기로 복귀)

    //손목 관절 hand link 가져오기
    ArticulationBody Wrist => ik.joints[ik.joints.Length - 1]; //손목 관절
    float WristAngle => Wrist.jointPosition[0] * Mathf.Rad2Deg;  //현재 손목 관절 각도 (도)

    // 로그용 손목각도 정보
    string WristInfo() => $"{WristAngle:F0}도 (한계 {Wrist.xDrive.lowerLimit:F0}~{Wrist.xDrive.upperLimit:F0}도)";

    private void Awake()
    {
        if (ik == null)
        {
            ik = GetComponent<ArmIK>();
        }
    }

    //Void Start 처럼 IEnumerator Start() 자동함수 
    IEnumerator Start()
    {
        IsBusy = true;

        //씬에 놓인 베이스가 BasePoint 근처(2cm)에 있으면 그것을 조립 대상으로 사용
        if (basePoint != null)
        {
            foreach (BaseInfo sceneBase in FindObjectsByType<BaseInfo>())
            {
                if ((sceneBase.transform.position - basePoint.position).sqrMagnitude <0.0004f)
                {
                    currentBase = sceneBase;
                    break;
                }
            }
        }

        yield return new WaitUntil(() => ik.Ready);
        Status = "대기 위치로";
        yield return GoHome();

        //대기 위치의 관절 각도를 저장-> 매번 이 자세로 돌아올수있도록 
        homeJoints = new float[ik.joints.Length];

        //미션1 
        //반복문을 사용하여 homeJoint 배열에 ik joint의 xDrive target 값을 넣어주기 
        for (int jointIndex = 0; jointIndex < homeJoints.Length; jointIndex++)
        {
            homeJoints[jointIndex] = ik.joints[jointIndex].xDrive.target;
        }

        Debug.Log($"[AssemblySequence] 대기 자세 저장 | 손목 관절 {WristInfo()}");
        Status = "대기";
        IsBusy = false;
    }

    // GoHome이 필요한 이유
    // 손목 관절은 돌수있는 범위가 정해져있음 -148~148
    //조립이 끝나면 손목 관절이 범위 끝에 가있을수도있음
    //이상태로 MoveTo로 대기위치, 손목0도 -> ik는 지금 자세에서 가장 가까운 답을 찾음
    // 그리퍼는 위에서 봤을떄 180도 돌려도 같기에 0도와 180도를 같은 답으로 인식할 가능성이있음
    // 그래서 손이 대기 위치에 왔을떄 손목 관절 끝 각도에 남아있을수도있음
    // 다음 조립떄 손목을 한쪽으로 거의 못돌려서 실패가능성을 챙김
    //그 각도까지 한번에 튀지 않도록 부드럽게 이동, 실제로 도착됐는지 확인하는 부분
    IEnumerator GoHome()
    {
        //홈 각도가 없거나 관절 수와 다르면 홈으로 이동한 뒤 다시 저장
        if (homeJoints == null || homeJoints.Length != ik.joints.Length)
        {
            yield return ik.MoveTo(homePosition, 0f);

            homeJoints = new float[ik.joints.Length];
            for (int jointIndex = 0; jointIndex < homeJoints.Length; jointIndex++)
            {
                homeJoints[jointIndex] = ik.joints[jointIndex].xDrive.target;
            }

            yield break;
        }
        // 1단계 이동시간 정하기 
        //지금 각 관절의 목표를 출발 각도로 기록
        // 저장 각도까지 가장 많이 돌아야하는 관절이 몇도인지 구한다 
        ArticulationBody[] joints = ik.joints; //관절 6개
        float[] startAngles = new float[joints.Length];
        float maxTurn = 0f;//가장 많이 돌아야하는 관절 각도 

        for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
        {
            startAngles[jointIndex] = joints[jointIndex].xDrive.target;


            //가장 많이 돌아야하는 각도 찾기
            maxTurn = Mathf.Max(maxTurn, Mathf.Abs(homeJoints[jointIndex] -  startAngles[jointIndex]));
        }

        //이동시간 구하기
        //이동시간 = 가장 많이 도는 관절의 각도 / 관절 속도
        //예 손목이 135도 돌아야하고 속도가 90도/초 => 1.5초
        //모든 관절이 이 시간동안 같이 출발해서 도착한다.
        float duration = Mathf.Max(0.5f, maxTurn / Mathf.Max(1f, homeJointSpeed));

        //2단계 출발 각도에서 저장각도까지 조금씩 옮기기
        //progress 0에서 출발해서 1에서 도착
        //Time.fixedDeltaTime으로 duration에 맞춰 조금씩 커짐
        //blend progress를 SmoothStep으로 바꾼값. 처음과 끝에는 천천히, 가운데 빠르게 움직임. 
        //Lerp(출발, 저장, blend) blend가 0이라면 출발 가곧, 1이면 저장각도 0.5면 그중간
        //ArticulationDrive 값을 넣어줌. ArticulationDrive는 struct라서 값을 적용할때는 꺼내서 고친 뒤 다시 넣어줘야함 

        float progress = 0f;

        while (progress < 1f)
        {
            progress += Time.fixedDeltaTime / duration;                      //물리 한칸만큼 진행률을 조금 올림 

            float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));

            for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
            {
                ArticulationDrive drive = joints[jointIndex].xDrive;

                drive.target = Mathf.Lerp(
                    startAngles[jointIndex],
                    homeJoints[jointIndex],
                    blend
                );

                joints[jointIndex].xDrive = drive;
            }
            yield return new WaitForFixedUpdate(); // 물리 한칸 기다렸다가 다음 각도로 
        }

        //3단계 실제로 도착할떄까지 기다리지
        //target 각도 = 이동해야할 지점
        //jointPosition = 실제 각도 
        // 모든 관절의 실제 각도가 저장각도와 0.5도 안으로 들어오면 끝 
        //최대 1.5초까지만 기다림

        for (float waited = 0f; waited < 1.5; waited+= Time.fixedDeltaTime)
        {
            bool allArrived = true;

            for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
            {
                //Mathf.Abs(실제 각도 - 저장 각도) > 0.5f
                if (Mathf.Abs(joints[jointIndex].jointPosition[0]* Mathf.Rad2Deg - homeJoints[jointIndex]) >0.5f)
                {
                    allArrived=false; // 하나라도 아직 도착 안했다면 더 기다림
                    break;
                }
            }

            if (allArrived)
            {
                break;
            }

            yield return new WaitForFixedUpdate();
        }
    }

    #endregion

    #region 2. 부품과 베이스 준비
    [Header("프리팹")]
    [UnityEngine.Serialization.FormerlySerializedAs("partPreFabs")]
    public PartInfo[] partPrefabs;     //PartInfo가 붙은 부품 프리팹
    public BaseInfo[] basePrefabs;     //BaseInfo 붙은 부품 프리팹

    [Header("베이스 배치 흔들기 { 컨베이어로 베이스가 들어올떄 약가의 어긋남 재현")]
    public bool randomzeBase = true;        // 베이스를 놓을때 위치,방향을 무작위로 
    public float baseOffsetRange = 0.005f;  //BasePoint에서 x,z 각각 +- 이 거리안에서 무작위
    public bool baseRightAngles = true;     //켜져있으면 베이스를 직각으로 돌린다 0, 90, 180, 270도

    [Header("부품 생성 범위 PartStopPoint 기준")]
    public float spawnRangeX = 0.01f;     // +- 1cm로 벨트가 멈추는 위치의 흔들림 
    public float spawnRangeZ = 0.03f;     // +-3cm 벨트 가장자리에 손가락이 닿지 않는 범위 
    [UnityEngine.Serialization.FormerlySerializedAs("randonRotation")]
    public bool randomRotation = true;    // 끄면 회전 각도 0
    public float settleTime = 0.5f;       // 생성 후 안착 대기 (초)

    private Rigidbody currentPart;        //현재 부품
    private PartInfo currentPartInfo;     //현재 부품의 정보

    public PartInfo CurrentPart => currentPartInfo;
    public BaseInfo CurrentBase => currentBase;

    //벨트 위 조립된 부품 정리 (잡고있는 부품 그대로)
    public void ClearPart()
    {
        if (currentPart != null)
        {
            //미션1
            //그리퍼가 잡고있는 부품이 현재부품과 같다면 return
            if (gripper.HeldPart == currentPart) return;

            //미션2 
            //현재 부품을 카메라에 남지않게 즉시 숨기
            currentPart.gameObject.SetActive(false);

            //미션3 
            //현재 부품을 삭제
            Destroy(currentPart.gameObject);
        }
        currentPart = null;
        currentPartInfo = null;
    }

    //부품 생성 : PartStopPart 주변 무작위 위치, 무작위 회전 
    public void SpawnPart(int index)
    {
        //부품 생성 못하는 조건이라면 return
        if (partPrefabs == null || index <0 || index >= partPrefabs.Length) return;

        ClearPart();

        //생성하기
        PartInfo prefab = partPrefabs[index];
        Vector3 stopPosition = partStopPoint.position;

        Vector3 spawnPosition = new Vector3(stopPosition.x + Random.Range(-spawnRangeX, spawnRangeX),
                                            stopPosition.y + prefab.halfHeight + 0.002f,    //벨트 윗면 + 부품 절반 + 2mm위에서 떨어뜨림 
                                            stopPosition.z + Random.Range(-spawnRangeZ, spawnRangeZ));

        float spawnYaw = randomRotation ? Random.Range(0f, 180f) : 0f;

        //미션
        //생성한 값을 spawndPart변수로 만들기
        //생성 Instantiate 함수 사용. 어떤 프리팹을 생성할지, 위치, 회전값       
        //currentPart = Instantiate(prefab, spawnPosition, spawnRotation);
        PartInfo spawndPart = Instantiate(prefab, spawnPosition, Quaternion.Euler(0, spawnYaw, 0));
        currentPartInfo = spawndPart;
        currentPart = spawndPart.GetComponent<Rigidbody>();
        Status = $"{prefab.name} 생성 (회전 {spawnYaw:F0}°)";
    }

    //베이스는 basePoint에 지정한 베이스를 놓음. randomizeBase라면 위치와 방향을 조금 흔듦
    public void SpawnBase(int index)
    {
        //1. 베이스 생성이 가능한지
        if (basePrefabs == null || index <0 || index >= basePrefabs.Length)
        {
            return;
        }
        //2. 현재 베이스가 존재한다면, 카메라에서 숨겨주고, 현재 베이스는 삭제
        if (currentBase != null)
        {
            currentBase.gameObject.SetActive(false);
            Destroy(currentBase.gameObject);
        }
        //3. randomizeBase가 true라면 새로운 포지션 x,y,z,값을 구합니다
        //y값은 90도 기준으로함 베이스는 0,90,180,270 -> Random.Range(0,4)
        Vector3 spawnPosition = basePoint.position;
        float spawnYaw = 0f;

        if (randomzeBase)
        {
            spawnPosition.x += Random.Range(-baseOffsetRange, baseOffsetRange);

            spawnPosition.z += Random.Range(-baseOffsetRange, baseOffsetRange);

            if (baseRightAngles)
            {
                spawnYaw = 90f * Random.Range(0, 4);
            }
        }
        //4. 현재 베이스를 생성 
        //Status 값을 업데이트
        currentBase = Instantiate(basePrefabs[index], spawnPosition, Quaternion.Euler(0f, spawnYaw, 0f));

        Status = $"{basePrefabs[index].name}배치 (회전 {spawnYaw:F0})";
    }

    //부품에 맞는 베이스를 고를 때 사용 
    public int FindBaseIndex(PartType type)
    {
        for (int i = 0; i < basePrefabs.Length; i++)
        {
            if (basePrefabs[i] != null &&  basePrefabs[i].holeType == type)
            {
                return i;
            }
        }
        return -1; // ??????????? 값이 없거나 초기화전이면 이런데는디 
    }
    #endregion

    #region 3. 잡을 손목 각도
    [Header("손목 각도")]
    public float yawStep = 15f;
    public int yawCount = 12;    //0도, 15, 165도

    //부품 방향에 맞는 손목 각도 고르기 
    //후보 12개를 하나씩 부품 방향과 비교하여 차이가 가장 적은 것 고르기 

    public float HeuristicGraspYaw(PartInfo part)
    {
        float symmetry = part.symmetryDeg;
        float partYaw = part.transform.eulerAngles.y; //위에서 본 부품 방향
        float bestYaw = 0f;                           // 지금까지 찾은 가장 좋은 손목 각도 
        float bestError = float.MaxValue;

        for (int action = 0; action < yawCount; action++)
        {
            float yaw = action * yawStep;  //후보각도 0,15~ 165

            //후보각도와 부품 방향의 차이 구하기 
            //그냥 빼면 안됨 대칭각도 계산해야함
            float error = Mathf.Abs(Mathf.Repeat(yaw - partYaw + symmetry * 0.5F, symmetry) - symmetry*0.5f);
            // Mathf.Abs()는 절댓값함수  Mathf.Repeat(값, 길이)는 값이 0 이상, 길이 미만에 들어오도록 길이만큼 되감는 함수

            if (error < bestError)
            {
                bestError = error;
                bestYaw = yaw;
            }
        }
        return bestYaw;
    }
    #endregion

    #region 4. 조립 계산
    //GripPoint : 로봇 손의 기준점
    //FlatDelta : 위에서 본 두점 사이의 거리 ( 높이는 무시)
    //PartHoleYawErr : 부품을 구멍 방향에 맞추려면 손목 몇도더 돌려야하나 
    //CheakSeated : 다 놓고 난 뒤 제대로 끼워졌나 확인 

    [Header("끼움 판정")]
    //높이: 부품이 구멍 바닥까지 내려갔는지 3mm 넘게 떠있으면 실패 (구멍에 덜 들어가고 위에 얹힌경우) 
    public float seatHeightTolerance = 0.003f;

    //위치: 부품 중심이 구멍 중심에 있는지. 4mm 넘게 옆으로 비껴있으면 실패
    public float seatCenterTolerance = 0.004f;

    //기울기: 부품이 똑바로 서있는지 5도 넘게 기울이면 실패 ( 구멍 모서리에 걸려서 비스듬한 경우)
    public float seatTiltTolerance = 5f;

    Transform GripPoint => gripper.attachPoint != null ? gripper.attachPoint : ik.tip;  //로봇 손 기준점

    //베이스 위에서 손이 내려갈때, 얼마나 옆으로 치우쳤는지를 잰다
    //예를 들어서 오른쪽으로 3mm 치우쳤다면 -> 왼쪽으로 3mm 옮긴다
    //수평면에서 a-b(높이 차이는 버림)
    static Vector3 FlatDelta(Vector3 a, Vector3 b)
    {
        float differenceX = a.x - b.x;//좌우 차이
        float differenceZ = a.z - b.z;//앞뒤 차이

        return new Vector3(differenceX, 0, differenceZ);
    }

    //부품 방향과 베이스 구멍 방향 사이 (대칭 고려)
    //예 큐브(대칭90) -> 100도, 구멍 0->+10도
    static float PartHoleYawErr(PartInfo part, BaseInfo baseInfo)
    {
        float symmetry = part.symmetryDeg;    // 이만큼 돌리면 같은 모양 큐브 90 삼각기둥 180
        float halfSymmetry = symmetry * 0.5f; //대칭의 절반 : 큐브 45, 삼각기둥 90
        float partYaw = part.transform.eulerAngles.y; //부품 방향(위에서 본 각도)
        float holeYaw = baseInfo.HoleYaw;

        //1. 그냥 차이값 구하기 
        float rawDifference = partYaw - holeYaw;
        //2. 대칭 단위로 접어서- 절반 ~+절반 범위로 
        //접는다 = 큰 각도를 같은 모양인 작은 각도로 변한다
        return Mathf.Repeat(rawDifference + halfSymmetry, symmetry) - halfSymmetry; // Mathf.Repeat(a,b) a가 b미만으로 반복해서 값나옴

    }

    //끼움 판정 : 높이, 중심, 기울기 세가지 기준이 충족할때 성공
    //판정 결과를 전달 (성공 여부 bool, 판정에 결과 문장 string)
    private bool CheakSeated(PartInfo part, BaseInfo baseInfo, out string info)
    {
        Transform partTransform = part.transform;

        //1. 높이 : 부품 바닥이 베이스의 구멍 바닥에 닿았는지 
        float bottom = partTransform.position.y - part.halfHeight; //부품 바닥 높이
        //베이스 구멍 바닥과의 차이 구하기
        float heightError = bottom - baseInfo.FloorY;

        //2. 중심
        //옆에서 봤을때 부품 중심과 베이스 구멍중심이 얼마나 떨어져있는지 
        Vector2 centerOffset = new Vector2(partTransform.position.x - baseInfo.HoleCenter.x,
            partTransform.position.z - baseInfo.HoleCenter.z);

        //3. 기울기
        //부품의 위쪽 방향이 하늘과 몇도 벌어졌는지 똑바로 서있다 0도
        float tilt = Vector3.Angle(partTransform.up, Vector3.up);

        //4. 위에 네가지를 모두 통과하면 성공
        bool okHeight = Mathf.Abs(heightError) <= seatHeightTolerance;

        //magnitude 두점 사이의 직선 거시
        //좌우 3mm, 앞뒤 4mm 떨어져있으면, 실제 거리는 5mm 
        bool okCenter = centerOffset.magnitude <= seatCenterTolerance;

        bool okTilt = tilt <= seatTiltTolerance;

        bool ok = okHeight && okTilt && okCenter;

        //1000을 곱해서 m -> mm, F1 소수 첫째자리까지 표시 
        info = $"{(ok ? "끼움 성공" : "끼움 실패")} | 높이 {heightError * 1000f:F1}mm, 중심 {centerOffset.magnitude * 1000f:F1}mm"
                + $", 기울기 {tilt:F1}도";

        //부품과 베이스 종류가 안맞으면 표시하기
        if (!baseInfo.Accepts(part))
        {
            info += "(틀린 조합)";
        }
        return ok;
        #endregion
    }

    #region 5.손목 돌리기(관절 한계 관리)
    //부품을 잡은 채 베이스 위로 가면, 부품 방향과 베이스방향이 다를수있다
    //손목을 돌려서 맞춰야하는데, 손목 관절은 돌수있는 범위가 정해져있음
    //큐브는 90도 마다 같은 모양임. +30도 돌리기, -60도 돌리기가 같은 결과.
    //관절 범위 안이면서 가장 적게 도는 쪽을 고르기 

    public float wristLimitMargin = 10f;        //손목을 끝까지 돌리지 않고 10도 여유를 남김
    public float wristStepDeg = 60;             //손목을 크게 돌릴때 한번에 돌릴수있는 최대 각도(90도 미만이여야 원하는 방향으로 돌림)

    //손목에 각도 제한이 있는지 
    //true면 범위 한계가 정해져있다는 뜻 
    bool wristLimited => Wrist.xDrive.lowerLimit < Wrist.xDrive.upperLimit;

    //손목 모터를 +1도 돌렸다  시계 방향을 돌것인지 반시계방향으로 돌것인지
    //결과값은 +1 아니면-1인지
    private float WristYawRate()
    {
        ArticulationBody wrist = Wrist;

        //손목 회전축 ( 월드)
        Vector3 axisWorld = wrist.transform.rotation * (wrist.anchorRotation* Vector3.right);

        //부호값을 구할때 => Mathf.Sign
        //Dot는 내적값 구하기 = 손목 회전축이 위쪽을 얼마나 향하고있는지 ( 두방향이 얼마나 같은쪽을 보는지 숫자로 보여줌)
        Mathf.Sign(Vector3.Dot(axisWorld, Vector3.up));

        //Arm ik가 확인한 모터 방향
        float sign =
       (ik.jointSigns != null &&
        ik.jointSigns.Length == ik.joints.Length) ? ik.jointSigns[ik.joints.Length - 1] : 1f;

        //모터 설정 방향(jointSigns)와 출방향 곱하기
        //둘다 +1 ->1, 하나라도 -1이면 -1이 최종값
        return sign * Mathf.Sign(Vector3.Dot(axisWorld, Vector3.up));
    }

    //손목 관절 각도가 한계치를 얼마나 넘어가는지 (안넘어간다 = 0)
    float WristOverLimit(float angle)
    {
        if (!wristLimited)
        {
            return 0f;
        }

        //실제 한계값에서 여유 10도씩 안쪽으로 줄인 범위. 손목 관절을 끝까지 쓰지 않게 하기위함
        float lowest = Wrist.xDrive.lowerLimit + wristLimitMargin;      //-148 + 10 = -138
        float highest = Wrist.xDrive.upperLimit - wristLimitMargin;     //148-10 = 138

        //Mathf.Max(0f, lowest - angle) -138보다 얼마나 아래로 내려갔는지  // 안내려갔으면 음수. max에서 결과없이 0
        //Mathf.Max(0f, angle - highest);위쪽 138보다 얼마나 더 올라갔는디 구함 안올라가면 0
        //Mathf.Max(0f, lowest - angle) Mathf.Max()는 두 값 중 큰 값을 고름
        return Mathf.Max(0f, lowest - angle) + Mathf.Max(0f, angle - highest);
    }

    //부품을 구멍 방향에 맞추려면 손목을 몇도 돌릴지 고르는 함수
    //돌리는 방법이 여러개이니 그중 손목 한계에 안걸리며너, 가장 적게 도는 방법을 찾는다
    //큐브 90도마다 같은 모양. 부품 구멍에 맞출떄 +30 -60 =120도
    //rotation는 필요한 회전
    //symmetry 부품 대칭 각도
    //avoidDirection 피할 방향. 0이면 안피하고 +1이면 +1방향은 금지 -1이면 -방향은 금지

    float ChooseRotation(float rotation, float symmetry, float avoidDirection = 0f)
    {
        float rate = WristYawRate();                 // 위에서 본1도 => 모터의 +1도 -1도인지
        float nowAngle = WristAngle;                 // 지금 손목 모터 각도
        float bestRotation = rotation;               // 일단 원래 회전을 답으로 두기 
        float bestScore = float.MaxValue;           // 점수는 아주나쁜값으로 시작. 가장 적게 도는 방법을찾은것이니 점수가 작을수록 좋음

        //같은 결과를 내는 회전을 알아보자
        //후보 만들기 : 필요한 회전에는 대칭각도를 -2번 ~ 2번 더한다
        //예 큐브(대칭90) 필요 회전 +30도 -> -150, -60, +30, +120, +210
        for (int symmetryTimes = -2; symmetryTimes<=2; symmetryTimes++)
        {
            float candidate = rotation + symmetryTimes * symmetry;

            //200도가 넘으면 후보에서 뺸다
            if ((Mathf.Abs(candidate)> 200f))
            {
                continue;
            }

            if (avoidDirection != 0f && Mathf.Sign(candidate) == avoidDirection && Mathf.Abs(candidate)>1f)
            {
                continue;
            }

            //점수 = 한계초과량 * 1000 + 회전 크기
            //nowAngle + candidate * rate 이 후보대로 돌린 뒤의 예상 모터 각도
            //1도만 넘어도 1000점 이상 -> 한계를 넘는 후보는 사실상 탈락. 
            //넘지 않는다면 그중 가장 적게도는 쪽이 이긴다
            float score = WristOverLimit(nowAngle + candidate * rate) * 1000f + Mathf.Abs(candidate);
            if (score < bestScore)
            {
                bestScore = score;
                bestRotation = candidate;
            }
        }
        return bestRotation;
    }

    // 손목을 크게 돌려야할때 한번에 돌리지 않고, 60도 이하로 나눠서 돌리는 함수 
    IEnumerator RotateWrist(Vector3 position, float fromYaw, float rotation)
    {
        //Mathf.Abs돌릴 양의 크기
        //Mathf.Max한번에 돌릴 최대 각도
        //Mathf.CeilToInt()올림해서 정수로 만듬 -> ()안에있는 걸 소수점 자리 올려서 정수만듬

        //몇번에 나눌지 정하기
        int stepCount = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(rotation) / Mathf.Max(10f, wristStepDeg)));

        for (int i = 1; i <= stepCount; i++)  //1,2,3
        {
            yield return ik.MoveTo(position, fromYaw + rotation *i / stepCount);
        }

    }
    #endregion

    #region 6. 진단 로그 조립 한번의 기록
    //조립 중 단계마다 기록을 모아두고 finish됐을때 [AssemblyDiag]로 출력
    public bool logDiagnostics = true;

    StringBuilder diagnosticLog = new StringBuilder();

    //기록 추가. 각 줄의 사이에 | 문자 넣기 
    private void AddDiagnostic(string text)
    {
        // diagnosticLog.AppendLine(text);
        if (diagnosticLog.Length > 0)
        {
            diagnosticLog.Append("|");
        }
        diagnosticLog.AppendLine(text);
    }

    //IK 결과 한줄로 출력, 계산 성공 여부, 위치 오차, 손목 오차
    private string IkInfo()
    {
        return $"IK {(ik.LastSolveOk ? "OK" : "닿지않음")} 위치오차 {ik.PositionError * 1000f:F1}mm"
            + $" 손목 오차 {ik.YawError:F1}도";
    }

    //FloatDelta는 치우침의 정도를 알려줌 x좌우 z 앞뒤
    //그런데 손목은 매번 다른 각도로 돌아가있으니 월드 x,z만으로는 그리퍼 입장에서 어느쪽으로 밀렸는지 알기 어려움
    //1. 닫힘축(손가락이 마주 닫히는 방향) -> 한쪽 손가락이 먼저 닿아서 부품을 밀었다 ( 그리퍼가 닫힐때 양쪽이 고르게 안닫혔는대,
    //잡는 위치가 한쪽으로 치우쳤을떄
    //2. 직각 (손이 부품 정중앙 위로 오지 못함)

    //손안에서 부품이 치우친 정도를 "닫는 방향 성분 / 직각 성분"으로
    private string HeldOffsetInfo(Vector3 flatOffset, float wristYaw)
    {
        //손가락이 닫히는 방향 구하기 
        Vector3 closeAxis = Quaternion.Euler(0, wristYaw, 0)*Vector3.forward;

        //직각 방향 구하기
        //Vector3.Cross(a,b) 외적은 a와b 둘다 직각인 방향을 줌
        Vector3 side = Vector3.Cross(Vector3.up, closeAxis);

        //치우침을 두 방향으로 나누기 
        //Vector3.Dot(치우침, 방향) 치우침 중에서 그 방향으로 얼마나 벗어났는지 구해요. 이때 방향 벡터의 길이는 1이어야 해요.
        //Vector3.Dot(flatOffset, closeAxis) * 1000f;//닫힘축 방향으로 몇mm
        return $"닫힘축 {Vector3.Dot(flatOffset, closeAxis) * 1000f:F1}mm /직각 {Vector3.Dot(flatOffset, side) * 1000f:F1}mm";
    }

    #endregion

    #region 7.조립 동작(핵심)
    [Header("높이")]
    public float pickAboveY = 0.16f;
    public float placeAboveY = 0.17f;

    public float fingerTipDepth = 0.021f;  //그리퍼 기준점(attach point)에서 손가락 끝까지 어래 방향 거리
    public float releaseClearance = 0.002f; //놓는 순간 손가락 끝과 베이스 윗면 사이에 여유

    [Header("속도")]
    public float travelSpeed = 0.15f;
    public float approachSpeed = 0.1f;
    public float insertSpeed = 0.02f;  //베이스 위에서 놓는 높이까지 내려가는 속도 
    public float releaseWait = 0.5f;   //연 뒤 부품이 앉을때까지 대기 

    [Header("놓기 전에 손목 보정")]
    //손목을 돌린뒤에 부품방향과 구멍 방향이 이값보다 어긋나면 다시 손목을 맞춤
    public float yawAlingTolerance = 2f;
    //손 안에서 부품이 치우친만큼 놓는 위치를 옮겨서 부품 중심을 구멍 중심에 맞춘다
    public bool compensateHeldOffset = true;

    public float warndistance = 0.003f;//보정 후에도 이만큼 넘게 어긋났으면 경고
    public float warnYaw = 4f;

    //조립결과 (다른 스클비트에서 읽음)
    public bool LastGraspOK { get; private set; }  //마지막 조립에서 잡아 들어올렸는지

    public bool LastSeated { get; private set; }  // 마지막 조립에서 끼워졌는지

    public string LastSeatInfo { get; private set; } = "-"; //끼움 판정 문장

    //조립 시도 수
    private int attemptCount;
    //끼움 성공 수
    private int successCount;

    //graspYaw 잡을 손목 각도 // ? null값까지 사용
    //null이면 부품 회전으로 계산 (HeuristicGraspYaw)
    //잡기 -> 베이스 위로 -> 손목 맞추기 -> 내려가기 -> 놓기 -> 빠지기 -> 판정 -> 복귀 
    public IEnumerator Assemble(float? graspYaw = null)
    {
        if (IsBusy)
        {
            yield break;
        }

        IsBusy = true;
        LastGraspOK = false;
        LastSeated = false;
        LastSeatInfo = "-";
        diagnosticLog.Clear();

        //현재 부품이 없거나 부품 정보가 없다면
        //Status부품 없음으로 변경
        //작업중 false
        //코루틴을 멈춤
        // private Rigidbody currentPart;        //현재 부품
        //private PartInfo currentPartInfo;     //현재 부품의 정보
        if (currentPart == null || currentPartInfo ==null)
        {
            Status = "부품 없음";
            IsBusy = false;
            yield break;
        }

        //현재 베이스가 없다면
        //Status베이스가 없음
        // 작업중 false
        //코루틴을 멈추기
        if (currentBase == null)
        {
            Status = "베이스 없음";
            IsBusy = false;
            yield break;
        }

        PartInfo part = currentPartInfo;
        BaseInfo baseInfo = CurrentBase;

        float yaw = graspYaw ?? HeuristicGraspYaw(part); //?왼쪽이 null이면 오른쪽 값을 사용한다
        float symmetry = part.symmetryDeg;

        //잡기
        Vector3 partPosition = currentPart.position;
        Vector3 pickAbove = new Vector3(partPosition.x, pickAboveY, partPosition.z); //부품 위 대기 위치
        Vector3 pick = partPosition + Vector3.up * part.gripOffset;  // 잡는 위치는 부품마다 높이가 다름 
        AddDiagnostic($"잡기 : 부품 {part.transform.eulerAngles.y:F1}도 손목 {yaw:F0}");


        //1.부품위로
        Status = $"1. 부품위로 (손목{yaw}도)";
        //ik.moveto 함수 호출
        yield return ik.MoveTo(pickAbove, yaw);
        AddDiagnostic($"부품 위 {IkInfo()}");

        //2.내려가기
        //status 입력
        //ik 이동함수 호출
        Status = "2. 부품위로 내려가기";
        yield return ik.MoveLinear(pick, approachSpeed, yaw);
        Vector3 pickOffset = FlatDelta(currentPart.position, GripPoint.position);
        AddDiagnostic($"내려간 뒤 {IkInfo()}, 손 부품 중심{pickOffset.magnitude * 1000: F1}mm {HeldOffsetInfo(pickOffset, yaw)}");

        //3.잡기
        Status = "3. 잡기";
        yield return gripper.Close();

        //미션1
        //현재 그리퍼가 잡은 부품이 없다
        //잡기 실패를 status
        //다시 올라가기 
        //그리퍼를 열기 
        //GoHome 함수를 호출
        //코루틴을 종료
        if (gripper.HeldPart == null)
        {
            Status = $"잡기 실패 -{gripper.LastGraspInfo}";

            yield return ik.MoveLinear(pickAbove, approachSpeed, yaw);

            yield return gripper.Open();
            yield return GoHome();

            //TODO Finish 함수 호출하기
            Finish(false, "잡기 실패");
            //IsBusy = false;
            yield break;
        }



        //4.들어올리기
        //들어올리는 함수를 호출
        //그리퍼에 현재 잡은 부품이 없다면 
        //GoHome 호출
        //코루틴 종료
        Status = "4.들어올리기";

        yield return ik.MoveLinear(pickAbove, approachSpeed, yaw);

        if (gripper.HeldPart == null)
        {
            yield return GoHome();

            //TODO Finish 함수 호출하기
            Finish(false, "들어올리는 중에 놓침");

            yield break;
        }

        LastGraspOK = true;

        float partYawLift = part.transform.eulerAngles.y;
        Vector3 heldOffset = FlatDelta(part.transform.position, GripPoint.position); //손 안에서 부품이 치우친 정도
        AddDiagnostic($"들어올린 뒤 부품{partYawLift:F1}도, 손 안의 치우침 {heldOffset.magnitude * 1000f: F1}mm" +
            $"{HeldOffsetInfo(heldOffset, yaw)}), 손목 관절 {WristInfo()}");

        //2.베이스로 이동
        Vector3 hole = baseInfo.HoleCenter;
        Vector3 placeAbove = new Vector3(hole.x, pickAbove.y, hole.z);
        float releaseY = baseInfo.TopY + releaseClearance + fingerTipDepth;     // 손가락이 베이스 윗면 2mm위에 오는 높이 

        Status = "5. 베이스 위로 이동";
        yield return ik.MoveLinear(placeAbove, travelSpeed, null); //손목 각도를 자유롭게 둔 채 이동

        //지금 손목 각도 = 잡은 손목 각도 + 부품이 돈 만큼 (부품은 손에 고정되어서 같이 돌아감)
        //현재 손목 각도 알아내야함
        //yaw  = 처음 잡을때 손목 각도         (예 45)
        //partYawLift: 들어올린 직후 부품 방향 (예 47)
        //part.transform.eulerAngles.y:지금 부품 회전 방향(예 54도)
        // Mathf.DeltaAngle(47,54) -> 7도 
        //Mathf.DeltaAngle 각도는 360에서 0도로 넘어간다. 350도에서 10도로 가면 그냥 빼면 -340. 실제로는 +20도
        System.Func<float> ActualWrist = () => yaw + Mathf.DeltaAngle(partYawLift, part.transform.eulerAngles.y);

        float placeYaw = ActualWrist();
        float yawError = PartHoleYawErr(part, baseInfo);//부품방향과 구멍 방향의 차이 
        AddDiagnostic($"베이스 위 손목 {placeYaw:F1}도, 손목 관절{WristInfo()}, 부품-구멍 각도{yawError:F1}도");


        //3. 손목을 돌려서 부품방향과 구멍방향 맞추기 (최대 2번)
        //관절 한계 때문에 크게 못미친다면, 다음 시도에서는 반대방향으로돌린다
        //반복 최대 2번, 각도가 2도 넘게 틀린 동안 
        //1. 돌릴 방향과 양을 고르기
        //2. 나눠서 돌리기
        //3. 다시 재기: 지금은 몇 도 틀렸는지
        //4. 결과에 따라서 2도 이하 -> 성공 끝
        //                15도 미만 -> 다시 돌림. 미세 조정
        //                15도 이상 -> 크게 못미침. 반대방향으로 한번더

        float avoidDirection = 0f; //피할 방향. 처음에는 값 없음

        for (int attempt = 0; attempt < 2 && Mathf.Abs(yawError)> yawAlingTolerance; attempt++)
        {
            float rotation = ChooseRotation(-yawError, symmetry, avoidDirection);
            float predicted = WristAngle + rotation * WristYawRate(); //돌린 뒤 예상 모터 각도

            Status = $"6.손목 {rotation:F0}도 돌리기";
            yield return RotateWrist(placeAbove, placeYaw, rotation);

            avoidDirection = Mathf.Sign(rotation);   //방금 돈 방향을 기억한다(+1, -1). 기억해놔야 다음시도할때 이방법을 피하고 반대로 시도

            //다시 재기
            placeYaw = ActualWrist();                   //돌린 뒤 지금 손목 각도
            yawError = PartHoleYawErr(part, baseInfo);  //돌린 뒤 지금 차이
            AddDiagnostic($"회전 {attempt + 1} {rotation:F1}도 예상 관절{predicted:F0}도 ->"+
                $"실제 {WristAngle:F0}도 -> {IkInfo()}, 각도{yawError:F1}도");

            //결과 판단 
            if (Mathf.Abs(yawError) <= yawAlingTolerance)   //2도 이하 -> 성공
            {
                break;
            }

            if (Mathf.Abs(yawError) <15f)
            {
                //조금 모자라면 실제 손목각도 기준으로 미세보정
                Status = "6-1 미세 손목 조정";
                yield return ik.MoveTo(placeAbove, placeYaw-yawError);
                placeYaw = ActualWrist();
                yawError = PartHoleYawErr(part, baseInfo);
                AddDiagnostic($"미세 보정 -> {IkInfo()}, 각도{yawError:F1}도");
                break;
            }

            //오차가 크다면 다른 방향을 다시 고름 
        }

        //3-1 손안에서 부품이 치우친만큼 위치를 옮겨서 부품 중심을 구멍위에 갈수있도록함 
        //부품 손 가운데에서 옆으로 얼마나 치우쳤는지 잼 (shift)
        //그만큼 손을 반대로 옮긴다 -> 부품 중심이 구멍 위로 온다
        // 잘 맞았난 확인해서 기록
        //놓을 위치도 똑같이 옮겨줌

        Vector3 shift = Vector3.zero;  //치우침을 저장. 보정을 안하면 0 그대로
        Vector3 partToHole;            //부품과 구멍의 수평거리 (확인용)

        if (compensateHeldOffset)
        {
            shift = FlatDelta(part.transform.position, GripPoint.position);
            Vector3 aligendAbove = placeAbove - shift; //placeAbove 바로 위
            yield return ik.MoveLinear(aligendAbove, approachSpeed, placeYaw);

            //옮긴 뒤 부품 중심과 구멍 중심이 몇 mm 떨어졌는지 체크
            partToHole = FlatDelta(part.transform.position, hole);
            AddDiagnostic($"위치 보정{shift.magnitude * 1000f:F1}mm -> {IkInfo()}," +
                $"거리{partToHole.magnitude * 1000f:F1}mm");
        }
        Vector3 release = new Vector3(hole.x-shift.x, releaseY, hole.z - shift.z);

        //4. 베이스까지 내려가기
        Status = "7.놓는 높이까지 내려가기";

        yield return ik.MoveLinear(release, insertSpeed, placeYaw);

        yawError = PartHoleYawErr(part, baseInfo);
        partToHole = FlatDelta(part.transform.position, hole);

        bool stillHeld = gripper.HeldPart !=null;
        //놓기 직전에 정렬이 됐는지 검사하고 안됐으면 경고 
        bool alignedBeforeRelease = partToHole.magnitude <= warndistance && Mathf.Abs(yawError) <= warnYaw;

        AddDiagnostic($"놓기 직전 {IkInfo()}, 각도 {yawError:F1}도 거리{partToHole.magnitude* 1000f: F1}mm"+
            (stillHeld ? "" : "(이미 놓침")+ (alignedBeforeRelease ? "" : "정렬 경고"));

        //5. 놓기
        Status = "8.놓기";
        yield return gripper.Open();
        yield return new WaitForSeconds(releaseWait);

        //6.위로 빠지기
        Status = "9.위로 빠지기";
        yield return ik.MoveLinear(new Vector3(release.x, placeAboveY, release.z), approachSpeed, placeYaw);

        //7.끼움 판정
        bool seated = CheakSeated(part, baseInfo, out string info);

        //8.대기 위치로 이동
        Status = "10. 대기 위치로";
        yield return GoHome();

        // TODO Finish 함수 만들기
        Finish(seated, info);
    }

    private void Finish(bool seated, string info)
    {
        LastSeated = seated;
        LastSeatInfo = info;

        attemptCount++;

        if (seated)
        {
            successCount++;
        }

        Status = info;

        Debug.Log($"[Assembly] {info} | 부품{(currentPartInfo != null ? currentPartInfo.partType.ToString() : "-")},"
                    + $"베이스{(currentBase != null ? currentBase.holeType.ToString() : "-")}");

        if (logDiagnostics && diagnosticLog.Length >0)
        {


            string line = $"[AssemblyDiag] {(seated ? "성공" : "실패")} " +
                $"{(currentPartInfo != null ? currentPartInfo.partType.ToString() : "-")} | {diagnosticLog}";

            if (seated)
            {
                Debug.Log(line);
            }
            else
            {
                Debug.LogWarning(line);
            }
        }
        IsBusy = false;
    }
    #endregion

    #region  8.학습용 집기 테스트
    //학습용: 잡고 들어올려서 잡혔는지 확인 
    //잡은 뒤에는 그 자리에서 놓고 다시 대기 자세로 복귀함

    public IEnumerator PickTest(float graspYaw)
    {
        // ---- 시작 준비 (Assemble 맨 앞과 같음) ----
        // TODO 1: 이미 동작 중이면 바로 끝낸다 (if문 + yield break)
        if (IsBusy)
        {
            yield break;
        }

        IsBusy = true;
        LastGraspOK = false;
        LastSeated = false;
        // TODO 2: 동작 중 표시를 켜고, 지난 결과(LastGraspOk, LastSeated)를 false로 지운다

        // TODO 3: 부품이 없으면 Status에 "부품 없음"을 쓰고, 동작 중 표시를 끄고 끝낸다
        if (currentPart == null || currentPartInfo == null)
        {
            Status = "부품없음";
            IsBusy = false;
            yield break;
        }

        // ---- 위치 계산 (Assemble과 같음) ----
        // TODO 4: 부품 위치를 partPosition에 저장한다
        // TODO 5: 부품 위 대기 위치 pickAbove를 만든다 (x, z는 부품, 높이는 pickAboveY)
        // TODO 6: 잡는 위치 pick을 만든다 (부품 중심에서 위로 gripOffset만큼)
        //         주의: Assemble의 part 대신 여기서는 currentPartInfo를 쓴다
        Vector3 partPosition = currentPart.position;

        Vector3 pickAbove = new Vector3(
            partPosition.x,
            pickAboveY,
            partPosition.z
        );

        Vector3 pick =
            partPosition + Vector3.up * currentPartInfo.gripOffset;


        // ---- 잡기 ----
        Status = $"[학습] 잡기 (손목 {graspYaw:F0}도)";
        // TODO 7: 부품 위로 이동 (MoveTo, 손목 각도 graspYaw)
        yield return ik.MoveTo(pickAbove, graspYaw);
        // TODO 8: 잡는 위치까지 직선으로 내려가기 (MoveLinear, approachSpeed)
        yield return ik.MoveLinear(pick, approachSpeed, graspYaw);
        // TODO 9: 그리퍼 닫기
        yield return gripper.Close();

        // ---- 들어 올리고 결과 확인 ----
        // TODO 10: 잡혔으면(gripper.HeldPart가 null이 아니면)
        //            부품 위로 직선으로 들어 올리고,
        //            들어 올린 뒤에도 잡혀 있는지를 LastGraspOk에 저장한다
        //          못 잡았으면 그냥 부품 위로 올라간다
        if (gripper.HeldPart != null)
        {
            yield return ik.MoveLinear(pickAbove, approachSpeed, graspYaw);
             LastGraspOK=gripper.HeldPart !=null;
        }
        else
        {
            yield return ik.MoveLinear(pickAbove, approachSpeed, graspYaw);
        }


        // ---- 정리 ----
        // TODO 11: 그리퍼를 연다 (부품은 벨트 위로 떨어짐. 다음 생성 때 정리됨)
        yield return gripper.Open();
        // TODO 12: 대기 위치로 돌아간다 (GoHome)
        yield return GoHome();
        // TODO 13: Status에 성공이면 "[학습] 잡기 성공", 실패면 "[학습] 잡기 실패"를 쓴다 (조건 ? A : B)
        Status = LastGraspOK ? "[학습] 잡기 성공": "[학습] 잡기 실패";
        // TODO 14: 동작 중 표시를 끈다
        IsBusy = false;
    }

    #endregion

    #region 9. 테스트 화면

    [Header("테스트")]
    public bool showTestButtons = true;

    bool autoRunning;

    IEnumerator AutoRun(int count)
    {
        autoRunning = true;
        for (int i = 0; i < count; i++)
        {
            int partIndex = Random.Range(0, partPrefabs.Length);
            SpawnPart(partIndex);
            int baseIndex = FindBaseIndex(partPrefabs[partIndex].partType);
            if (baseIndex >= 0)
            {
                SpawnBase(baseIndex);
            }
            yield return new WaitForSeconds(settleTime);
            yield return Assemble();
            yield return new WaitForSeconds(0.3f);
        }
        autoRunning = false;
        Status = $"자동 {count}회 끝: {successCount}/{attemptCount}";
    }

    void OnGUI()
    {
        if (!showTestButtons)
        {
            return;
        }

        GUILayout.BeginArea(new Rect(10, 215, 320, 260), GUI.skin.box);
        GUILayout.Label($"상태: {Status}");
        GUILayout.Label($"결과: {successCount}/{attemptCount} 성공");

        GUI.enabled = !IsBusy && !autoRunning && ik != null && ik.Ready && !ik.IsMoving;

        //BeginHorizontal 프리팹마다 버튼을 하나씩 가로로 만든다
        GUILayout.BeginHorizontal();  //부품 생성 버튼
        if (partPrefabs != null)
        {
            for (int i = 0; i < partPrefabs.Length; i++)
            {
                if (partPrefabs[i] != null && GUILayout.Button($"{partPrefabs[i].name} 생성"))
                {
                    SpawnPart(i);
                }
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (basePrefabs != null)
        {
            for (int i = 0; i < basePrefabs.Length; i++)
            {
                if (basePrefabs[i] != null && GUILayout.Button($"{basePrefabs[i].name} 배치"))
                {
                    SpawnBase(i);
                }
            }
        }
        GUILayout.EndHorizontal();

        randomRotation = GUILayout.Toggle(randomRotation, "부품 무작위 회전");

        if (GUILayout.Button("조립 실행"))
        {
            StartCoroutine(Assemble());
        }
        if (GUILayout.Button("자동 10회 (맞는 조합)"))
        {
            StartCoroutine(AutoRun(10));
        }
        if (GUILayout.Button("결과 초기화"))
        {
            attemptCount = 0;
            successCount = 0;
        }

        GUI.enabled = true;
        GUILayout.EndArea();
    }

    #endregion
}

